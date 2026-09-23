using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Core.Events;

/// <summary>
/// Session 标题变更事件 - 标题自动生成完成时发出
/// </summary>
public record SessionTitleChangedEvent : IMessageEvent
{
    /// <summary>会话 ID。</summary>
    public required string SessionId { get; init; }
    /// <summary>触发标题变更的执行循环 ID（可选）。</summary>
    public string? LoopId { get; init; }
    /// <summary>事件时间戳（UTC）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>事件类型标识。</summary>
    public string Type => "session.title.changed";

    /// <summary>更新后的标题</summary>
    public required string Title { get; init; }
}
