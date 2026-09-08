namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 WebSocket 客户端运行时选项
/// </summary>
public sealed class WeComWsClientOptions
{
    public required string BotId { get; init; }

    public required string Secret { get; init; }

    public string WsUrl { get; init; } = "wss://openws.work.weixin.qq.com";

    public int HeartbeatIntervalSeconds { get; init; } = 30;

    public int MaxReconnectAttempts { get; init; } = -1;
}
