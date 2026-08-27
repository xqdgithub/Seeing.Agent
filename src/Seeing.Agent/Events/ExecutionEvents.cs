using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Events;

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

/// <summary>
/// 命令执行结果事件 - 内置命令执行完成后发出
/// </summary>
public record CommandResultEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.CommandResult;
    
    /// <summary>命令名称</summary>
    public required string CommandName { get; init; }
    
    /// <summary>是否成功</summary>
    public bool Success { get; init; }
    
    /// <summary>结果消息</summary>
    public string? Message { get; init; }
    
    /// <summary>导航目标（可选）</summary>
    public string? NavigationTarget { get; init; }

    /// <summary>是否需要前端刷新时间线（压缩等变更会话内容的命令）</summary>
    public bool NeedsRefresh { get; init; }

    /// <summary>
    /// 是否继续执行 Agent（false = 命令要求结束本轮：shouldContinue=false 或 shouldExit）。
    /// 宿主据此短路：不再把命令文本作为普通消息发送给大模型。
    /// </summary>
    public bool ShouldContinue { get; init; } = true;
}
