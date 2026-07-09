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

/// <summary>心跳投递目标</summary>
public static class HeartbeatTargets
{
    public const string Main = "main";
    public const string Last = "last";
    public const string Inbox = "inbox";
}
