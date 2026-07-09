using System.Text.Json.Serialization;

namespace Seeing.Agent.Scheduler.Models;

/// <summary>调度器配置</summary>
public class SchedulerOptions
{
    /// <summary>是否启用调度器</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>时区 ID（如 Asia/Shanghai），默认本地时区</summary>
    public string Timezone { get; set; } = TimeZoneInfo.Local.Id;

    /// <summary>全局最大并发任务数</summary>
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>持久化配置</summary>
    public PersistenceOptions Persistence { get; set; } = new();

    /// <summary>心跳配置</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();
}

/// <summary>持久化配置</summary>
public class PersistenceOptions
{
    /// <summary>是否启用持久化</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>持久化提供程序：sqlite / memory</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>数据库连接字符串</summary>
    public string? ConnectionString { get; set; }

    /// <summary>表名前缀</summary>
    public string TablePrefix { get; set; } = "QRTZ_";
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

    /// <summary>心跳 Prompt（必填，支持 Markdown 格式）</summary>
    public string Prompt { get; set; } = "";

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

    /// <summary>一次性执行时间（本地时间）</summary>
    public DateTime? RunAt { get; set; }

    public string Timezone { get; set; } = TimeZoneInfo.Local.Id;
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
    
    /// <summary>调度意图（用户配置）</summary>
    [JsonPropertyName("intent")]
    public ScheduleIntent Intent { get; set; } = ScheduleIntent.Active;

    /// <summary>向后兼容：旧版 Enabled 字段</summary>
    [JsonPropertyName("enabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    [Obsolete("Use Intent instead")]
    public bool? LegacyEnabled 
    { 
        get => null;
        set => _legacyEnabled = value;
    }
    
    [JsonIgnore]
    private bool? _legacyEnabled;
    
    /// <summary>迁移意图（从旧版 Enabled 字段）</summary>
    internal void MigrateIntent()
    {
        if (_legacyEnabled.HasValue)
        {
            Intent = _legacyEnabled.Value ? ScheduleIntent.Active : ScheduleIntent.Disabled;
            _legacyEnabled = null;
        }
    }

    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = ScheduleTaskTypes.Agent;

    public string? Text { get; set; }
    public string? Prompt { get; set; }
    public string? Agent { get; set; }

    public ScheduleSpec Schedule { get; set; } = new();
    public DispatchSpec Dispatch { get; set; } = new();
    public JobRuntimeSpec Runtime { get; set; } = new();

    public DateTime? LastRunAt { get; set; }
}

/// <summary>jobs.json 根对象</summary>
public class JobsFile
{
    /// <summary>版本号（用于迁移，默认 0 表示需要迁移）</summary>
    public int Version { get; set; } = 0;
    
    public List<ScheduledJobSpec> Jobs { get; set; } = new();
    
    /// <summary>迁移旧版本数据</summary>
    internal void MigrateIfNeeded()
    {
        if (Version < 2)
        {
            foreach (var job in Jobs)
                job.MigrateIntent();
            Version = 2;
        }
    }
}

/// <summary>执行历史记录</summary>
public class JobExecutionRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string JobId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt - StartedAt;
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? Output { get; set; }
    public string Source { get; set; } = ScheduleSources.Cron;
    
    // === 执行快照参数 ===
    
    /// <summary>任务类型 (agent/text)</summary>
    public string? TaskType { get; set; }
    
    /// <summary>执行时的 Agent ID</summary>
    public string? Agent { get; set; }
    
    /// <summary>执行时的 Prompt 内容</summary>
    public string? Prompt { get; set; }
    
    /// <summary>执行时的 Text 内容</summary>
    public string? Text { get; set; }
    
    /// <summary>执行时的 Session ID</summary>
    public string? SessionId { get; set; }
    
    /// <summary>投递渠道</summary>
    public string? DispatchChannel { get; set; }
    
    /// <summary>投递用户 ID</summary>
    public string? DispatchUserId { get; set; }
    
    /// <summary>投递会话 ID</summary>
    public string? DispatchSessionId { get; set; }
}

/// <summary>任务执行结果</summary>
public class JobExecutionResult
{
    public bool Success { get; init; }
    public string? RunId { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string TaskType { get; init; } = ScheduleTaskTypes.Agent;
    public string Source { get; init; } = ScheduleSources.Cron;
    public string? Agent { get; init; }
    public string? SessionId { get; init; }
    
    /// <summary>执行时的 Prompt 内容</summary>
    public string? Prompt { get; init; }
    
    /// <summary>执行时的 Text 内容</summary>
    public string? Text { get; init; }
    
    /// <summary>投递渠道</summary>
    public string? DispatchChannel { get; init; }
    
    /// <summary>投递用户 ID</summary>
    public string? DispatchUserId { get; init; }
    
    /// <summary>投递会话 ID</summary>
    public string? DispatchSessionId { get; init; }
}

/// <summary>投递请求</summary>
public class DispatchRequest
{
    public required string Source { get; init; }
    public required string TaskType { get; init; }
    public required string Content { get; init; }
    public string? UserInput { get; init; }
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
