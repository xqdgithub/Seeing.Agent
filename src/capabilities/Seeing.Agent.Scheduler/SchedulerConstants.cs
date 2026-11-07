namespace Seeing.Agent.Scheduler;

/// <summary>调度模块常量</summary>
public static class SchedulerConstants
{
    /// <summary>心跳内部 Job ID</summary>
    public const string HeartbeatJobId = "_heartbeat";

    /// <summary>历史记录上限</summary>
    public const int MaxHistoryRecords = 50;

    /// <summary>默认任务超时（秒）</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>默认 misfire grace（秒）</summary>
    public const int DefaultMisfireGraceSeconds = 300;

    // ===== Quartz 相关常量 =====

    /// <summary>默认 Job 组名</summary>
    public const string DefaultJobGroup = "DEFAULT";

    /// <summary>默认 Trigger 组名</summary>
    public const string DefaultTriggerGroup = "DEFAULT";

    /// <summary>Quartz 表名前缀</summary>
    public const string QuartzTablePrefix = "QRTZ_";
}

/// <summary>任务来源</summary>
public static class ScheduleSources
{
    public const string Cron = "cron";
    public const string Heartbeat = "heartbeat";
    public const string Manual = "manual";
}

/// <summary>任务类型</summary>
public static class ScheduleTaskTypes
{
    public const string Text = "text";
    public const string Agent = "agent";
}

/// <summary>调度类型</summary>
public static class ScheduleTypes
{
    public const string Cron = "cron";
    public const string Interval = "interval";
    public const string Once = "once";
}

/// <summary>任务 id 保留策略：内部任务 id 不得经用户任务 API 进入 Jobs。</summary>
public static class ScheduleIdPolicy
{
    /// <summary>
    /// 是否为平台保留 id。
    /// <para>
    /// 保留 id 属于内部任务（目前是心跳），只能经各自的服务入口操作，
    /// 不得通过用户任务 API 进入 <c>JobsFile.Jobs</c>——否则它会出现在定时任务列表、
    /// cron 工具与统计里，并与内部槽位争抢同一个 Quartz job key。
    /// </para>
    /// </summary>
    public static bool IsReserved(string? jobId)
        => string.Equals(jobId, SchedulerConstants.HeartbeatJobId, StringComparison.Ordinal);
}
