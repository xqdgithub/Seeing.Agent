namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台任务状态
/// </summary>
public enum BackgroundTaskStatus
{
    /// <summary>待执行</summary>
    Pending,
    
    /// <summary>正在执行</summary>
    Running,
    
    /// <summary>已完成</summary>
    Completed,
    
    /// <summary>执行失败</summary>
    Failed,
    
    /// <summary>已取消</summary>
    Cancelled
}

/// <summary>
/// 后台任务信息
/// </summary>
public class BackgroundTaskInfo
{
    /// <summary>任务 ID</summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Agent 名称</summary>
    public string AgentName { get; set; } = string.Empty;
    
    /// <summary>任务状态</summary>
    public BackgroundTaskStatus Status { get; set; }
    
    /// <summary>开始时间</summary>
    public DateTimeOffset StartedAt { get; set; }
    
    /// <summary>完成时间</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    
    /// <summary>执行结果</summary>
    public string? Result { get; set; }
    
    /// <summary>错误信息</summary>
    public string? Error { get; set; }
    
    /// <summary>会话 ID</summary>
    public string? SessionId { get; set; }
    
    /// <summary>父会话 ID</summary>
    public string? ParentSessionId { get; set; }
    
    /// <summary>任务描述</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 后台任务启动参数
/// </summary>
public class BackgroundTaskLaunchArgs
{
    /// <summary>Agent 名称</summary>
    public required string AgentName { get; set; }
    
    /// <summary>输入消息</summary>
    public required Llm.ChatMessage Input { get; set; }
    
    /// <summary>执行上下文</summary>
    public required Models.AgentContext Context { get; set; }
    
    /// <summary>任务描述（可选，用于状态显示）</summary>
    public string? Description { get; set; }
}