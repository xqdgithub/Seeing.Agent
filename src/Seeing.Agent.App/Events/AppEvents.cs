using Seeing.Agent.Core.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.App.Events;

/// <summary>
/// App 层事件类型（扩展值范围：1000+，避免与 Core 层冲突）
/// </summary>
public enum AppEventType
{
    /// <summary>Session 更新</summary>
    SessionUpdated = 1000,
    
    /// <summary>命令执行结果</summary>
    CommandResult = 1001,
    
    /// <summary>导航请求</summary>
    Navigate = 1002,
    
    /// <summary>Skill 内容展开</summary>
    SkillContent = 1003,
}

/// <summary>
/// Session 更新事件 - 会话数据变更时发出
/// </summary>
public record SessionUpdatedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public MessageEventType Type => (MessageEventType)AppEventType.SessionUpdated;
    
    /// <summary>更新后的 Session 数据</summary>
    public required SessionData Session { get; init; }
}

/// <summary>
/// 命令执行结果事件 - 内置命令执行完成后发出
/// </summary>
public record CommandResultEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public MessageEventType Type => (MessageEventType)AppEventType.CommandResult;
    
    /// <summary>命令名称</summary>
    public required string CommandName { get; init; }
    
    /// <summary>是否成功</summary>
    public bool Success { get; init; }
    
    /// <summary>结果消息</summary>
    public string? Message { get; init; }
    
    /// <summary>导航目标（可选）</summary>
    public string? NavigationTarget { get; init; }
}

/// <summary>
/// 导航事件 - 请求前端导航到指定路径
/// </summary>
public record NavigateEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public MessageEventType Type => (MessageEventType)AppEventType.Navigate;
    
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
    public MessageEventType Type => (MessageEventType)AppEventType.SkillContent;
    
    /// <summary>原始命令内容</summary>
    public required string OriginalContent { get; init; }
    
    /// <summary>展开后的 Skill 内容</summary>
    public required string ExpandedContent { get; init; }
}