#define HAVE_REMOTE

#include <stdlib.h>
#include <stdio.h>
#include <pcap.h>
#include <Shlwapi.h>
#include <iphlpapi.h>

#include "RouterIPv4.h"
#include "LinkedListTargetSystems.h"
#include "LinkedListFirewallRules.h"
#include "Logging.h"
#include "NetworkHelperFunctions.h"
#include "PacketHandlerIPv4Forwarding.h"

#define MAX_INJECT_RETRIES 4


// Global/external variables
extern PSYSNODE gTargetSystemsList;
extern PRULENODE gFwRulesList;
extern SCANPARAMS gScanParams;


/*
 * Receive, parse, resend
 *
 */
DWORD PacketHandlerRouterIPv4(PSCANPARAMS lpParam)
{
  char cwd[MAX_BUF_SIZE + 1];
  char filter[MAX_BUF_SIZE + 1];
  DWORD retVal = 0;
  char pcapErrorBuffer[PCAP_ERRBUF_SIZE];
  int counter = 0;
  int ifcNum = 0;
  struct bpf_program ifcCode;
  unsigned int netMask = 0;
  int funcRetVal = 0;
  struct pcap_pkthdr *packetHeader = NULL;
  unsigned char *packetData = NULL;
  
  // Determine and print current working directory
  GetCurrentDirectory(sizeof(cwd) - 1, cwd);
  LogMsg(DBG_INFO, "PacketHandlerDP(): Working directory: \"%s\"", cwd);

  // Set exit function to trigger depoisoning functions and command.
  SetConsoleCtrlHandler((PHANDLER_ROUTINE)RouterIPv4_ControlHandler, TRUE);

  ZeroMemory(pcapErrorBuffer, sizeof(pcapErrorBuffer));
  //ZeroMemory(&gScanParams, sizeof(gScanParams));
  //CopyMemory(&gScanParams, lpParam, sizeof(gScanParams));

  // Open interface.
  if ((gScanParams.InterfaceReadHandle = pcap_open_live((char *)gScanParams.InterfaceName, 65536, PCAP_OPENFLAG_NOCAPTURE_LOCAL | PCAP_OPENFLAG_MAX_RESPONSIVENESS, PCAP_READTIMEOUT, pcapErrorBuffer)) == NULL)
  {
    LogMsg(DBG_ERROR, "PacketHandlerRouterIPv4(): Unable to open the adapter \"%s\"", gScanParams.InterfaceName);
    retVal = 5;
    goto END;
  }

  // MAC == LocalMAC and (IP == GWIP or IP == VictimIP
  gScanParams.InterfaceWriteHandle = gScanParams.InterfaceReadHandle;
  ZeroMemory(&ifcCode, sizeof(ifcCode));
  ZeroMemory(filter, sizeof(filter));

  _snprintf(filter, sizeof(filter) - 1, "ip && ether dst %s && not src host %s && not dst host %s && not port 53", gScanParams.LocalMacStr, gScanParams.LocalIpStr, gScanParams.LocalIpStr);
  netMask = 0xffffff; // "255.255.255.0"

  if (pcap_compile((pcap_t *)gScanParams.InterfaceWriteHandle, &ifcCode, (const char *)filter, 1, netMask) < 0)
  {
    LogMsg(DBG_ERROR, "PacketHandlerRouterIPv4(): Unable to compile the BPF filter \"%s\"", filter);
    retVal = 6;
    goto END;
  }

  if (pcap_setfilter((pcap_t *)gScanParams.InterfaceWriteHandle, &ifcCode) < 0)
  {
    LogMsg(DBG_ERROR, "PacketHandlerRouterIPv4(): Unable to set the BPF filter \"%s\"", filter);
    retVal = 7;
    goto END;
  }

LogMsg(DBG_INFO, "PacketHandlerRouterIPv4(): BPF filter: %s", filter);
  LogMsg(DBG_INFO, "PacketHandlerRouterIPv4(): Enter listening/forwarding loop.");
  while ((funcRetVal = pcap_next_ex((pcap_t*)gScanParams.InterfaceWriteHandle, (struct pcap_pkthdr **) &packetHeader, (const u_char **)&packetData)) >= 0)
  {
    if (funcRetVal == 1)
    {
      PacketForwarding_handler((unsigned char *)&gScanParams, packetHeader, packetData);
    }
  }

  if (funcRetVal < 0)
  {
    char *errorMsg = pcap_geterr(gScanParams.InterfaceWriteHandle);
    LogMsg(DBG_ERROR, "PacketHandlerRouterIPv4(): Listener stopped unexpectedly with return value: %d, %s", funcRetVal, errorMsg);
  }
  else
  {
    LogMsg(DBG_INFO, "PacketHandlerRouterIPv4(): Listener stopped regularly with return value: %d", funcRetVal);
  }

END:

  LogMsg(DBG_INFO, "PacketHandlerRouterIPv4(): Exit");

  return retVal;
}


/*
 * Callback function invoked by libpcap for every incoming packet
 *
 */
void PacketForwarding_handler(u_char *param, const struct pcap_pkthdr *pktHeader, const u_char *data)
{
  PSCANPARAMS scanParams = (PSCANPARAMS)param;
  int bytesSent = 0;
  PSYSNODE realDstSys = NULL;
  PRULENODE firewallRule = NULL;
  PACKET_INFO packetInfo;

  if (pktHeader == NULL || 
      pktHeader->len <= 0 || 
      data == NULL)
  {
    return;
  }

  ZeroMemory(&packetInfo, sizeof(packetInfo));
  PrepareDataPacketStructure(data,(PPACKET_INFO) &packetInfo);
  packetInfo.pcapDataLen = pktHeader->len;


  IpBin2String((unsigned char *)&packetInfo.ipHdr->daddr, (unsigned char *)packetInfo.dstIp, sizeof(packetInfo.dstIp) - 1);
  IpBin2String((unsigned char *)&packetInfo.ipHdr->saddr, (unsigned char *)packetInfo.srcIp, sizeof(packetInfo.srcIp) - 1);

  CopyMemory(&packetInfo.srcIpBin, &packetInfo.ipHdr->saddr, 4);
  CopyMemory(&packetInfo.dstIpBin, &packetInfo.ipHdr->daddr, 4);
  snprintf(packetInfo.logMsg, sizeof(packetInfo.logMsg) - 1, "%%-5s %-4s %-15s %5d -> %-15s %-5d    %5d bytes    %s",
    packetInfo.proto, packetInfo.srcIp, packetInfo.srcPort, packetInfo.dstIp,
    packetInfo.dstPort, packetInfo.pktLen, packetInfo.suffix);

  // Firewall checks
  if ((firewallRule = FirewallBlockRuleMatch(gFwRulesList, packetInfo.proto, packetInfo.srcIpBin, packetInfo.dstIpBin, packetInfo.srcPort, packetInfo.dstPort)) != NULL)
  {
    if (ProcessFirewalledData(&packetInfo, scanParams) == FALSE)
    {
      LogMsg(DBG_ERROR, "Unable to apply firewall rules");
    }
  }

  // Destination IP is GW
  else if (memcmp(&packetInfo.ipHdr->daddr, scanParams->GatewayIpBin, BIN_IP_LEN) == 0)
  {
    if (ProcessData2GW(&packetInfo, scanParams) == FALSE)
    {
      LogMsg(DBG_ERROR, "Unable to send DATA 2 GW");
    }


  // Destination is victim system
  }
  else if ((realDstSys = GetNodeByIp(gTargetSystemsList, (unsigned char *)&packetInfo.ipHdr->daddr)) != NULL)
  {
    if (ProcessData2Victim(&packetInfo, realDstSys, scanParams) == FALSE)
    {
      LogMsg(DBG_ERROR, "Unable to send DATA 2 VICTIM");
    }

  // Destination IP is not inside the Network range.
  // Forward packet to the GW
  }
  else if (ProcessData2Internet(&packetInfo, scanParams) == FALSE)
  {
    LogMsg(DBG_ERROR, "Unable to send DATA 2 INTERNET");
  }
  else
  {
    // Data successfully forwarded    
  }
}


BOOL ProcessData2Internet(PPACKET_INFO packetInfo, PSCANPARAMS scanParams)
{
  BOOL retVal = FALSE;

  CopyMemory(packetInfo->etherHdr->ether_dhost, scanParams->GatewayMacBin, BIN_MAC_LEN);
  CopyMemory(packetInfo->etherHdr->ether_shost, scanParams->LocalMacBin, BIN_MAC_LEN);
  LogMsg(DBG_INFO, packetInfo->logMsg, "OUT");

  return SendPacket(MAX_INJECT_RETRIES, scanParams->InterfaceWriteHandle, packetInfo->pcapData, packetInfo->pcapDataLen);
}


BOOL ProcessData2Victim(PPACKET_INFO packetInfo, PSYSNODE realDstSys, PSCANPARAMS scanParams)
{
  CopyMemory(packetInfo->etherHdr->ether_dhost, realDstSys->data.sysMacBin, BIN_MAC_LEN);
  CopyMemory(packetInfo->etherHdr->ether_shost, scanParams->LocalMacBin, BIN_MAC_LEN);
  LogMsg(DBG_INFO, packetInfo->logMsg, "IN");

  return SendPacket(MAX_INJECT_RETRIES, scanParams->InterfaceWriteHandle, packetInfo->pcapData, packetInfo->pcapDataLen);
}


BOOL ProcessData2GW(PPACKET_INFO packetInfo, PSCANPARAMS scanParams)
{
  CopyMemory(packetInfo->etherHdr->ether_dhost, scanParams->GatewayMacBin, BIN_MAC_LEN);
  CopyMemory(packetInfo->etherHdr->ether_shost, scanParams->LocalMacBin, BIN_MAC_LEN);
  LogMsg(DBG_INFO, packetInfo->logMsg, "GW");

  return SendPacket(MAX_INJECT_RETRIES, scanParams->InterfaceWriteHandle, packetInfo->pcapData, packetInfo->pcapDataLen);
}


BOOL ProcessFirewalledData(PPACKET_INFO packetInfo, PSCANPARAMS scanParams)
{
  LogMsg(DBG_INFO, packetInfo->logMsg, "BLOCK");

  return TRUE;
}


void PrepareDataPacketStructure(const u_char *data, PPACKET_INFO packetInfo)
{
  ZeroMemory(packetInfo, sizeof(PACKET_INFO));

  packetInfo->pcapData = (u_char *)data;
  packetInfo->etherHdr = (PETHDR)data;
  packetInfo->ipHdr = (PIPHDR)(data + 14);
  packetInfo->ipHdrLen = (packetInfo->ipHdr->ver_ihl & 0xf) * 4;

  if (packetInfo->ipHdr->proto == IP_PROTO_TCP) // TCP
  {
    packetInfo->tcpHdr = (PTCPHDR)((unsigned char*)packetInfo->ipHdr + packetInfo->ipHdrLen);
    strcat(packetInfo->proto, "TCP");

    packetInfo->pktLen = ntohs(packetInfo->ipHdr->tlen) - packetInfo->ipHdrLen - packetInfo->tcpHdr->doff * 4;
    packetInfo->dstPort = ntohs(packetInfo->tcpHdr->dport);
    packetInfo->srcPort = ntohs(packetInfo->tcpHdr->sport);
    snprintf(packetInfo->suffix, sizeof(packetInfo->suffix) - 1, "[%s%s%s%s%s%s]",
      packetInfo->tcpHdr->ack ? "a" : " ",
      packetInfo->tcpHdr->syn ? "s" : " ",
      packetInfo->tcpHdr->psh ? "p" : " ",
      packetInfo->tcpHdr->fin ? "f" : " ",
      packetInfo->tcpHdr->rst ? "r" : " ",
      packetInfo->tcpHdr->urg ? "u" : " ");
  }
  else if (packetInfo->ipHdr->proto == 17) // UDP
  {
    packetInfo->udpHdr = (PUDPHDR)((unsigned char*)packetInfo->ipHdr + packetInfo->ipHdrLen);
    strcat(packetInfo->proto, "UDP");
    packetInfo->pktLen = ntohs(packetInfo->udpHdr->ulen);
    packetInfo->dstPort = ntohs(packetInfo->udpHdr->dport);
    packetInfo->srcPort = ntohs(packetInfo->udpHdr->sport);
  }
  else if (packetInfo->ipHdr->proto == 1)
  {
    strcat(packetInfo->proto, "ICMP");
  }
  else
  {
    strcat(packetInfo->proto, "Unknown");
  }
}


BOOL SendPacket(int maxTries, LPVOID writeHandle, u_char *data, unsigned int dataSize)
{
  BOOL retVal = FALSE;
  int counter = 0;

  for (; counter < maxTries; counter++)
  {
    if (pcap_sendpacket(writeHandle, data, dataSize) == 0)
    {
      retVal = TRUE;
      break;
    }
  }

  return retVal;
}


BOOL RouterIPv4_ControlHandler(DWORD pControlType)
{
  switch (pControlType)
  {
    // Handle the CTRL-C signal. 
  case CTRL_C_EVENT:
    LogMsg(DBG_INFO, "Ctrl-C event : Exiting process");
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;

  case CTRL_CLOSE_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Close event : Exiting process");
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;

  case CTRL_BREAK_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Break event : Exiting process");
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;

  case CTRL_LOGOFF_EVENT:
    printf("Ctrl-Logoff event : Exiting process");
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;

  case CTRL_SHUTDOWN_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Shutdown event : Exiting process");
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;

  default:
    LogMsg(DBG_INFO, "Unknown event \"%d\" : Exiting process", pControlType);
    CloseAllPcapHandles();
    LogMsg(DBG_INFO, "Ctrl-C event : pcap closed");
    return FALSE;
  }
}


void CloseAllPcapHandles()
{
  if (gScanParams.PcapFileHandle != NULL)
  {
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.PcapFileHandle");
    pcap_breakloop(gScanParams.PcapFileHandle);
    pcap_close(gScanParams.PcapFileHandle);
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.PcapFileHandle done");
  }

  if (gScanParams.InterfaceWriteHandle != NULL)
  {
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.InterfaceWriteHandle");
    pcap_breakloop(gScanParams.InterfaceWriteHandle);
    pcap_close(gScanParams.InterfaceWriteHandle);
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.InterfaceWriteHandle done");
  }

  if (gScanParams.InterfaceReadHandle != NULL)
  {
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.InterfaceReadHandle");
    pcap_breakloop(gScanParams.InterfaceReadHandle);
    pcap_close(gScanParams.InterfaceReadHandle);
    LogMsg(DBG_INFO, "CloseAllPcapHandles(): Closing gScanParams.InterfaceReadHandle done");
  }
}
