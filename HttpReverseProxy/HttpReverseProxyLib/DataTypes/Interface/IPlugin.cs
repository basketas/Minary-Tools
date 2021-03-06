﻿namespace HttpReverseProxyLib.DataTypes.Interface
{
  using HttpReverseProxyLib.DataTypes.Class;
  using HttpReverseProxyLib.DataTypes.Enum;


  public class PluginProperties
  {

    #region MEMBERS

    public string Name { get; set; }

    public int Priority { get; set; }

    public string Version { get; set; }

    public string PluginDirectory { get; set; }

    public bool IsActive { get; set; }

    public IPluginHost PluginHost { get; set; }

    public ProxyProtocol SupportedProtocols { get; set; }

    #endregion


    #region PUBLIC

    public PluginProperties()
    {
    }

    #endregion

  }


  public interface IPlugin : System.IComparable<IPlugin>
  {

    PluginProperties Config { get; set; }

    void OnLoad(IPluginHost pluginHost);

    void OnUnload();

    PluginInstruction OnPostClientHeadersRequest(RequestObj requestObj);

    PluginInstruction OnPostServerHeadersResponse(RequestObj requestObj);

    void OnServerDataTransfer(RequestObj requestObj, DataChunk dataChunk);
  }
}
