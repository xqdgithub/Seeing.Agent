namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>Quartz JobDataMap 键常量</summary>
public static class JobDataKeys
{
    // ===== 任务定义 =====
    
    /// <summary>任务 ID</summary>
    public const string JobId = "jobId";
    
    /// <summary>任务类型（agent / text）</summary>
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
    
    // ===== 执行配置 =====
    
    /// <summary>Session ID</summary>
    public const string SessionId = "sessionId";
    
    /// <summary>超时秒数</summary>
    public const string TimeoutSeconds = "timeoutSeconds";
    
    /// <summary>是否后台运行</summary>
    public const string RunInBackground = "runInBackground";
    
    // ===== 投递配置 =====
    
    /// <summary>投递目标 Channel</summary>
    public const string DispatchChannel = "dispatchChannel";
    
    /// <summary>投递目标 UserId</summary>
    public const string DispatchUserId = "dispatchUserId";
    
    /// <summary>投递目标 SessionId</summary>
    public const string DispatchSessionId = "dispatchSessionId";
    
    /// <summary>投递元数据（JSON）</summary>
    public const string DispatchMeta = "dispatchMeta";
    
    // ===== 任务来源 =====
    
    /// <summary>任务来源（cron / heartbeat）</summary>
    public const string Source = "source";
    
    // ===== 心跳特有 =====
    
    /// <summary>Query 文件路径</summary>
    public const string QueryFile = "queryFile";
    
    /// <summary>投递目标（main / last / inbox）</summary>
    public const string HeartbeatTarget = "heartbeatTarget";
    
    /// <summary>活跃时段配置（JSON）</summary>
    public const string ActiveHours = "activeHours";
}
