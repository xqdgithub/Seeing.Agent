namespace Seeing.Agent.Scheduler.Models;

/// <summary>任务状态枚举</summary>
public enum JobState
{
    /// <summary>正常调度中</summary>
    Normal,
    /// <summary>已暂停</summary>
    Paused,
    /// <summary>正在执行</summary>
    Running,
    /// <summary>已完成（一次性任务）</summary>
    Completed,
    /// <summary>错误状态</summary>
    Error,
    /// <summary>阻塞中</summary>
    Blocked
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

    /// <summary>任务状态</summary>
    public JobState State { get; init; }

    /// <summary>上次执行时间（本地时间）</summary>
    public DateTime? PreviousFireTime { get; init; }

    /// <summary>下次执行时间（本地时间）</summary>
    public DateTime? NextFireTime { get; init; }

    /// <summary>距离下次执行的时间</summary>
    public TimeSpan? TimeUntilNext => NextFireTime.HasValue
        ? NextFireTime.Value - DateTime.Now
        : null;

    /// <summary>最后错误信息</summary>
    public string? LastError { get; init; }

    /// <summary>最后错误时间（本地时间）</summary>
    public DateTime? LastErrorTime { get; init; }

    /// <summary>触发器类型</summary>
    public string? TriggerType { get; init; }

    /// <summary>Cron 表达式（如果是 Cron 触发器）</summary>
    public string? CronExpression { get; init; }

    /// <summary>间隔（如果是 Simple 触发器）</summary>
    public TimeSpan? Interval { get; init; }
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
