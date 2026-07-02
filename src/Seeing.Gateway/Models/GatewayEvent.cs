namespace Seeing.Gateway.Models;

/// <summary>
/// 网关事件对象类型
/// </summary>
public enum GatewayEventObject
{
    Message,
    Content,
    Response,
    Permission,
    Error
}

/// <summary>
/// 网关事件状态
/// </summary>
public enum GatewayEventStatus
{
    Created,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 网关流式事件
/// </summary>
public record GatewayEvent
{
    public required GatewayEventObject Object { get; init; }

    public required GatewayEventStatus Status { get; init; }

    public required string SessionId { get; init; }

    public string? LoopId { get; init; }

    public GatewayEventData? Data { get; init; }

    /// <summary>事件时间戳（对齐 <see cref="Seeing.Agent.Core.Events.IMessageEvent.Timestamp"/>）</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>原始 MessageEventType 字符串，便于 Client 精确分支</summary>
    public string? SourceType { get; init; }
}
