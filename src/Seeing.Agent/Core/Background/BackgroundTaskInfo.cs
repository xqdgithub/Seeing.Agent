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

    // === 新增字段 ===

    /// <summary>进度百分比 (0-100)</summary>
    public int Progress { get; set; }

    /// <summary>进度消息</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>进度更新时间</summary>
    public DateTimeOffset? ProgressUpdatedAt { get; set; }

    /// <summary>输出行列表</summary>
    public List<string> OutputLines { get; set; } = new();
}

/// <summary>
/// 后台任务启动参数
/// </summary>
public class BackgroundTaskLaunchArgs
{
    /// <summary>
    /// 任务 ID（应等于 Child Session Id / task_id）。未提供时生成临时 Id（仅兼容旧调用）。
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>Agent 名称</summary>
    public required string AgentName { get; set; }

    /// <summary>输入消息</summary>
    public required Llm.ChatMessage Input { get; set; }

    /// <summary>执行上下文</summary>
    public required Models.AgentContext Context { get; set; }

    /// <summary>任务描述（可选，用于状态显示）</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 可选：统一 Loop 执行回调。提供时优先于内置 agent.ExecuteAsync 旁路。
    /// 返回最终结果文本。
    /// </summary>
    public Func<CancellationToken, Task<string>>? LoopRunner { get; set; }
}