﻿namespace HttpReverseProxy.Plugin.RequestRedirect
{
  using HttpReverseProxyLib.DataTypes;
  using HttpReverseProxyLib.DataTypes.Class;
  using HttpReverseProxyLib.DataTypes.Enum;
  using HttpReverseProxyLib.Exceptions;
  using System.Collections.Generic;
  using System.Text.RegularExpressions;


  public partial class RequestRedirect
  {
    /// <summary>
    ///
    /// </summary>
    /// <param name="pluginHost"></param>
    public PluginInstruction OnPostClientHeadersRequest(RequestObj requestObj)
    {
      PluginInstruction instruction = new PluginInstruction();
      instruction.Instruction = Instruction.DoNothing;

      if (requestObj == null)
      {
        throw new ProxyWarningException("The request object is invalid");
      }

      if (HttpReverseProxy.Plugin.RequestRedirect.Config.RequestRedirectRecords == null)
      {
        return instruction;
      }

      if (requestObj.ClientRequestObj.ClientRequestHeaders == null || 
          requestObj.ClientRequestObj.ClientRequestHeaders.Count <= 0)
      {
        return instruction;
      }

      if (!requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Host"))
      {
        return instruction;
      }

      string host = requestObj.ClientRequestObj.ClientRequestHeaders["Host"][0];
      string path = requestObj.ClientRequestObj.RequestLine.Path;

      foreach (DataTypes.RequestRedirectConfigRecord tmpRecord in HttpReverseProxy.Plugin.RequestRedirect.Config.RequestRedirectRecords)
      {
        string hostSearchPattern = "^" + Regex.Escape(tmpRecord.Host) + "$";
        string pathSearchPattern = "^" + Regex.Escape(tmpRecord.Path) + "$";

        if (Regex.Match(host, hostSearchPattern, RegexOptions.IgnoreCase).Success &&
            Regex.Match(path, pathSearchPattern, RegexOptions.IgnoreCase).Success)
        {
          this.pluginProperties.PluginHost.LoggingInst.LogMessage("Inject", ProxyProtocol.Undefined, Loglevel.Info, "RequestRedirect.OnPostClientHeadersRequest(): Requesting \"{0}{1}\" ---{2}--> \"{3}\"",
            host, path, tmpRecord.RedirectType, tmpRecord.ReplacementResource);
          instruction.Instruction = Instruction.RedirectToNewUrl;
          instruction.InstructionParameters.Data = tmpRecord.ReplacementResource;
          instruction.InstructionParameters.Status = tmpRecord.RedirectType;
          instruction.InstructionParameters.StatusDescription = tmpRecord.RedirectDescription;
          break;
        }
      }

      return instruction;
    }
  }
}

