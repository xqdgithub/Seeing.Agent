using Seeing.Agent.Abstractions.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// Session 更新事件 - 会话数据变更时发出
/// </summary>
public record SessionUpdatedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.SessionUpdated;

    /// <summary>更新后的 Session 数据</summary>
    public required SessionData Session { get; init; }
}

/// <summary>
/// 导航事件 - 请求前端导航到指定路径
/// </summary>
public record NavigateEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.Navigate;

    /// <summary>导航目标路径</summary>
    public required string Target { get; init; }
}

/// <summary>
/// Skill 内容展开事件 - Skill 命令展开后发出
/// </summary>
public record SkillContentEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.SkillContent;

    /// <summary>原始命令内容</summary>
    public required string OriginalContent { get; init; }

    /// <summary>展开后的 Skill 内容</summary>
    public required string ExpandedContent { get; init; }
}

/// <summary>
/// Event fired when execution starts.
/// </summary>
public record ExecutionStartedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    /// <remarks>历史兼容：Type 复用 <see cref="MessageEventType.LoopStart"/>。</remarks>
    public string Type => MessageEventType.LoopStart;

    public string ExecutionId { get; init; } = "";
}

/// <summary>
/// Event fired when execution completes.
/// </summary>
public record ExecutionCompleteEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    /// <remarks>历史兼容：Type 复用 <see cref="MessageEventType.LoopComplete"/>。</remarks>
    public string Type => MessageEventType.LoopComplete;

    public string ExecutionId { get; init; } = "";
    public ExecutionStatus Status { get; init; }
}
