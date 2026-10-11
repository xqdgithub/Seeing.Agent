namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 收件箱通知事件 - 跨会话消息投递到目标会话时发出
/// </summary>
public record InboxNoticeEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.InboxNotice;

    /// <summary>发送方类型: human | session | system</summary>
    public required string SenderKind { get; init; }

    /// <summary>发送方标识</summary>
    public string? SenderId { get; init; }

    /// <summary>来源标签</summary>
    public string? SourceTag { get; init; }

    /// <summary>通知种类</summary>
    public string? Kind { get; init; }

    /// <summary>通知正文</summary>
    public string? Body { get; init; }
}
