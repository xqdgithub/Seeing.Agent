namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// ChannelHost 进程管理用的精简数据模型。
/// </summary>
public sealed class ChannelHostEntry
{
    public string ChannelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public string PluginSpec { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string ConfigFilePath { get; set; } = "";
    public string GatewayBaseUrl { get; set; } = "";
}
