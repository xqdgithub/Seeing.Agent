namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>Quartz JobDataMap 键常量</summary>
public static class JobDataKeys
{
    // ===== 任务定义 =====
    
    /// <summary>任务 ID</summary>
    public const string JobId = "jobId";
    
    /// <summary>任务类型：agent / text</summary>
    public const string TaskType = "taskType";
    
    /// <summary>任务名称</summary>
    public const string JobName = "jobName";
    
    // ===== Agent 任务 =====
    
    /// <summary>Agent ID</summary>
    public const string AgentId = "agentId";
    
    /// <summary>Prompt 内容</summary>
    public const string Prompt = "prompt";
    
    /// <summary>文本内容（Text 类型任务）</summary>
    public const string Text = "text";
    
    // ===== 执行参数 =====
    
    /// <summary>Session ID</summary>
    public const string SessionId = "sessionId";
    
    /// <summary>超时秒数</summary>
    public const string TimeoutSeconds = "timeoutSeconds";
    
    /// <summary>是否后台运行</summary>
    public const string RunInBackground = "runInBackground";
    
    /// <summary>Misfire 宽限期（秒）</summary>
    public const string MisfireGraceSeconds = "misfireGraceSeconds";
    
    /// <summary>是否共享 Session</summary>
    public const string ShareSession = "shareSession";
    
    // ===== 投递配置 =====
    
    /// <summary>投递目标 SessionId</summary>
    public const string DispatchSessionId = "dispatchSessionId";
    
    /// <summary>投递元数据（JSON）</summary>
    public const string DispatchMeta = "dispatchMeta";
    
    // ===== 任务来源 =====
    
    /// <summary>任务来源：cron / heartbeat / manual</summary>
    public const string Source = "source";
    
    /// <summary>执行 ID（每次执行唯一）</summary>
    public const string RunId = "runId";
    
    // ===== 心跳特有 =====
    
    /// <summary>投递目标：main / last / inbox</summary>
    public const string HeartbeatTarget = "heartbeatTarget";
    
    /// <summary>活跃时段（JSON）</summary>
    public const string ActiveHours = "activeHours";

    /// <summary>调度时区 ID（墙钟展示用）</summary>
    public const string ScheduleTimezone = "scheduleTimezone";
}
