using System.Text.Json.Serialization;

namespace Seeing.Agent.Scheduler.Models;

/// <summary>调度器配置</summary>
public class SchedulerOptions
{
    /// <summary>是否启用调度器</summary>
    public bool Enabled { get; set; }

    /// <summary>时区 ID（如 Asia/Shanghai）</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>调度 tick 间隔（秒）</summary>
    public int TickIntervalSeconds { get; set; } = 1;

    /// <summary>全局最大并发任务数</summary>
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>心跳配置</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();
}

/// <summary>心跳配置</summary>
public class HeartbeatOptions
{
    /// <summary>是否启用心跳</summary>
    public bool Enabled { get; set; }

    /// <summary>执行间隔（如 6h、30m）</summary>
    public string Every { get; set; } = "6h";

    /// <summary>投递目标：main / last / inbox</summary>
    public string Target { get; set; } = HeartbeatTargets.Main;

    /// <summary>Query 文件路径（相对工作区根目录）</summary>
    public string QueryFile { get; set; } = "HEARTBEAT.md";

    /// <summary>使用的 Agent ID</summary>
    public string? Agent { get; set; }

    /// <summary>Session ID（Target=main 时使用）</summary>
    public string SessionId { get; set; } = "main";

    /// <summary>超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = SchedulerConstants.DefaultTimeoutSeconds;

    /// <summary>活跃时段</summary>
    public ActiveHoursOptions? ActiveHours { get; set; }
}

/// <summary>活跃时段配置</summary>
public class ActiveHoursOptions
{
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "22:00";
    public string? Timezone { get; set; }
}

/// <summary>调度规格</summary>
public class ScheduleSpec
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = ScheduleTypes.Cron;

    /// <summary>5 字段 Cron 表达式</summary>
    public string? Cron { get; set; }

    /// <summary>间隔字符串（如 6h、30m）</summary>
    public string? Every { get; set; }

    /// <summary>一次性执行时间（ISO8601）</summary>
    public DateTimeOffset? RunAt { get; set; }

    public string Timezone { get; set; } = "UTC";
}

/// <summary>投递目标</summary>
public class DispatchTarget
{
    public string? Channel { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>投递配置</summary>
public class DispatchSpec
{
    public DispatchTarget Target { get; set; } = new();
    public Dictionary<string, object>? Meta { get; set; }
}

/// <summary>运行时配置</summary>
public class JobRuntimeSpec
{
    public bool RunInBackground { get; set; }
    public int TimeoutSeconds { get; set; } = SchedulerConstants.DefaultTimeoutSeconds;
    public int MisfireGraceSeconds { get; set; } = SchedulerConstants.DefaultMisfireGraceSeconds;
    public bool ShareSession { get; set; }
}

/// <summary>定时任务定义</summary>
public class ScheduledJobSpec
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = ScheduleTaskTypes.Agent;

    public string? Text { get; set; }
    public string? Prompt { get; set; }
    public string? Agent { get; set; }

    public ScheduleSpec Schedule { get; set; } = new();
    public DispatchSpec Dispatch { get; set; } = new();
    public JobRuntimeSpec Runtime { get; set; } = new();

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>jobs.json 根对象</summary>
public class JobsFile
{
    public List<ScheduledJobSpec> Jobs { get; set; } = new();
}

/// <summary>执行历史记录</summary>
public class JobExecutionRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? Output { get; set; }
    public string Source { get; set; } = ScheduleSources.Cron;
}

/// <summary>任务执行结果</summary>
public class JobExecutionResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string TaskType { get; init; } = ScheduleTaskTypes.Agent;
    public string Source { get; init; } = ScheduleSources.Cron;
    public string? AgentName { get; init; }
    public string? SessionId { get; init; }
}

/// <summary>投递请求</summary>
public class DispatchRequest
{
    public required string Source { get; init; }
    public required string TaskType { get; init; }
    public required string Content { get; init; }
    public string? Channel { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>投递结果</summary>
public class DispatchResult
{
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    public static DispatchResult Ok() => new();
    public static DispatchResult Fail(string error) => new() { Success = false, Error = error };
}
