namespace Seeing.Agent.Scheduler.Models;

/// <summary>调度意图（用户配置，持久化存储）</summary>
public enum ScheduleIntent
{
    /// <summary>已禁用（用户明确关闭）</summary>
    Disabled,
    /// <summary>已暂停（用户临时暂停）</summary>
    Paused,
    /// <summary>正常调度</summary>
    Active
}

/// <summary>任务运行时状态（实时计算）</summary>
public enum JobState
{
    /// <summary>已禁用（Intent = Disabled）</summary>
    Disabled,
    /// <summary>已暂停（Intent = Paused）</summary>
    Paused,
    /// <summary>已调度（Intent = Active，未在执行）</summary>
    Scheduled,
    /// <summary>运行中（Intent = Active，正在执行）</summary>
    Running
}

/// <summary>调度器运行状态</summary>
public sealed class SchedulerStatus
{
    /// <summary>调度器是否正在运行</summary>
    public bool IsRunning { get; init; }
    
    /// <summary>调度器是否已启动（已初始化但可能暂停）</summary>
    public bool IsStarted { get; init; }
    
    /// <summary>调度器是否处于待机模式</summary>
    public bool IsStandby { get; init; }
    
    /// <summary>任务总数</summary>
    public int TotalJobs { get; init; }
    
    /// <summary>正在执行的任务数</summary>
    public int RunningJobs { get; init; }
    
    /// <summary>已暂停的任务数</summary>
    public int PausedJobs { get; init; }
    
    /// <summary>上次执行时间</summary>
    public DateTime? LastExecutionTime { get; init; }
    
    /// <summary>下次执行时间</summary>
    public DateTime? NextExecutionTime { get; init; }
    
    /// <summary>调度器启动时间</summary>
    public DateTime? StartTime { get; init; }
    
    /// <summary>线程池大小</summary>
    public int ThreadPoolSize { get; init; }
}

/// <summary>单个任务状态</summary>
public sealed class JobStatus
{
    /// <summary>任务 ID</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>任务名称</summary>
    public string? JobName { get; init; }

    /// <summary>任务分组</summary>
    public string? JobGroup { get; init; }

    /// <summary>任务状态（运行时）</summary>
    public JobState State { get; init; }

    /// <summary>上次执行时间（本地时间）</summary>
    public DateTime? PreviousFireTime { get; init; }

    /// <summary>下次执行时间（本地时间）</summary>
    public DateTime? NextFireTime { get; init; }

    /// <summary>距离下次执行的时间</summary>
    public TimeSpan? TimeUntilNext => NextFireTime.HasValue
        ? NextFireTime.Value - DateTime.Now
        : null;

    /// <summary>触发器类型</summary>
    public string? TriggerType { get; init; }

    /// <summary>Cron 表达式（如果是 Cron 触发器）</summary>
    public string? CronExpression { get; init; }

    /// <summary>最后执行 ID</summary>
    public string? LastRunId { get; init; }
    
    /// <summary>最后执行时间</summary>
    public DateTime? LastExecutionTime { get; init; }
    
    /// <summary>最后执行是否成功</summary>
    public bool? LastExecutionSuccess { get; init; }

    /// <summary>最后错误信息</summary>
    public string? LastError { get; init; }
}

/// <summary>任务状态变更事件参数</summary>
public sealed class JobStatusChangedEventArgs : EventArgs
{
    /// <summary>任务 ID</summary>
    public string JobId { get; init; } = string.Empty;
    
    /// <summary>旧状态</summary>
    public JobState OldState { get; init; }
    
    /// <summary>新状态</summary>
    public JobState NewState { get; init; }
    
    /// <summary>变更时间</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
    
    /// <summary>错误信息（如果有）</summary>
    public string? Error { get; init; }
}

/// <summary>触发结果（discriminated union）</summary>
public abstract record TriggerResult
{
    /// <summary>已接受，返回执行 ID</summary>
    public sealed record Accepted(string RunId) : TriggerResult;
    
    /// <summary>任务不存在</summary>
    public sealed record NotFound : TriggerResult;
    
    /// <summary>任务已禁用</summary>
    public sealed record Disabled : TriggerResult;
    
    /// <summary>冲突（如正在执行）</summary>
    public sealed record Conflict(string Reason) : TriggerResult;
}

/// <summary>运行时信息</summary>
public sealed class RunInfo
{
    /// <summary>执行 ID</summary>
    public string RunId { get; }
    
    /// <summary>开始时间（本地时间）</summary>
    public DateTime StartTime { get; }
    
    public RunInfo(string runId, DateTime startTime)
    {
        RunId = runId;
        StartTime = startTime;
    }
}

/// <summary>任务进度阶段</summary>
public enum JobProgressStage
{
    /// <summary>已触发</summary>
    Triggered,
    /// <summary>执行完成</summary>
    Completed,
    /// <summary>执行失败</summary>
    Failed,
    /// <summary>已取消</summary>
    Cancelled
}

/// <summary>任务进度事件参数</summary>
public sealed class JobProgressEventArgs : EventArgs
{
    /// <summary>任务 ID</summary>
    public string JobId { get; init; } = string.Empty;
    
    /// <summary>执行 ID</summary>
    public string? RunId { get; init; }
    
    /// <summary>进度阶段</summary>
    public JobProgressStage Stage { get; init; }
    
    /// <summary>执行结果（Completed/Failed 时）</summary>
    public JobExecutionResult? Result { get; init; }
    
    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
