namespace Seeing.Gateway.Client;

/// <summary>
/// Gateway Client 公共运行时选项（JSON 根节：<c>.seeing/gateway-clients/{channelId}.json</c> 的 Agent / Model）。
/// 与 Channel 专有节（如 WeCom）和 Gateway 连接节并列。
/// </summary>
public class GatewayClientCommonOptions
{
    /// <summary>本 Channel 默认使用的 Agent ID；未设置时由 Gateway 侧解析全局 DefaultAgent。</summary>
    public string? Agent { get; set; }

    /// <summary>本 Channel 默认使用的 Model ID；未设置时由 Gateway 侧 AgentSelectionResolver 解析。</summary>
    public string? Model { get; set; }

    /// <summary>ACP 透传 Agent 的 session mode；Native Agent 不使用。</summary>
    public string? Mode { get; set; }
}
