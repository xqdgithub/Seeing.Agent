using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.App.Events;

/// <summary>
/// App 层事件类型字符串常量（不并入 Abstractions，保持 Abstractions 不感知 App 层应用事件）
/// </summary>
public static class AppEventTypeConstants
{
    /// <summary>Session 更新</summary>
    public const string SessionUpdated = "session.updated";

    /// <summary>Skill 内容展开</summary>
    public const string SkillContent = "skill.content";

    /// <summary>Session 标题变更</summary>
    public const string SessionTitleChanged = "session.title.changed";
}

/// <summary>
/// Session 更新事件 - 会话数据变更时发出
/// </summary>
public record SessionUpdatedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => AppEventTypeConstants.SessionUpdated;
    
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
    public string Type => AppEventTypeConstants.SkillContent;
    
    /// <summary>原始命令内容</summary>
    public required string OriginalContent { get; init; }
    
    /// <summary>展开后的 Skill 内容</summary>
    public required string ExpandedContent { get; init; }
}