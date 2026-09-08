using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Core.Events;

/// <summary>
/// Session 标题变更事件 - 标题自动生成完成时发出
/// </summary>
public record SessionTitleChangedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => "session.title.changed";

    /// <summary>更新后的标题</summary>
    public required string Title { get; init; }
}
