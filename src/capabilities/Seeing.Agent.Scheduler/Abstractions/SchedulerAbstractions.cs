using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Abstractions;

/// <summary>任务持久化抽象</summary>
public interface IScheduleRepository
{
    Task<JobsFile> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(JobsFile jobs, CancellationToken ct = default);
    Task AppendHistoryAsync(string jobId, JobExecutionRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(string jobId, int limit, int offset = 0, CancellationToken ct = default);
}

// IScheduledJobDispatcher / DispatchRequest / DispatchResult → Seeing.Agent.Abstractions.Scheduling

/// <summary>任务执行监听器</summary>
public interface IJobExecutionListener
{
    /// <summary>任务开始执行时调用（同步，只更新内存）</summary>
    void OnJobStart(string jobId, string runId);

    /// <summary>任务执行完成时调用</summary>
    Task OnJobExecutedAsync(string jobId, JobExecutionResult result, CancellationToken ct = default);
}

/// <summary>调度管理抽象</summary>
public interface IScheduleManager
{
    bool IsStarted { get; }

    // ===== 生命周期管理 =====
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    // ===== 状态查询（新增）=====
    Task<SchedulerStatus> GetStatusAsync(CancellationToken ct = default);
    Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobStatus>> GetAllJobStatusesAsync(CancellationToken ct = default);

    // ===== 任务 CRUD =====
    Task<IReadOnlyList<ScheduledJobSpec>> ListJobsAsync(CancellationToken ct = default);
    Task<ScheduledJobSpec?> GetJobAsync(string jobId, CancellationToken ct = default);
    Task<ScheduledJobSpec> CreateOrReplaceJobAsync(ScheduledJobSpec job, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default);

    // ===== 任务控制 =====
    Task<TriggerResult> RunJobOnceAsync(string jobId, CancellationToken ct = default);
    Task SetJobIntentAsync(string jobId, ScheduleIntent intent, CancellationToken ct = default);
    Task PauseJobAsync(string jobId, CancellationToken ct = default);
    Task ResumeJobAsync(string jobId, CancellationToken ct = default);
    Task DisableJobAsync(string jobId, CancellationToken ct = default);
    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);
}

/// <summary>
/// 心跳的唯一外部入口。页面、<c>heartbeat_*</c> 工具与 <c>/heartbeat-run</c> 只依赖本接口；
/// 它内部转调调度管理器，不持有 Quartz 实例、不自己写 jobs.json。
/// </summary>
public interface IHeartbeatService
{
    /// <summary>当前快照：槽位副本、真实调度状态、最近一次执行、调度器是否已启动。</summary>
    Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>局部更新；未提供的字段保持不变。校验失败抛 <see cref="ArgumentException"/>。</summary>
    Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default);

    /// <summary>立即执行一次。未配置/未启动/已禁用/正在执行各有明确结果。</summary>
    Task<TriggerResult> RunNowAsync(CancellationToken ct = default);

    /// <summary>取消正在执行的心跳。</summary>
    Task<bool> CancelAsync(CancellationToken ct = default);

    /// <summary>执行历史（倒序）。</summary>
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit, int offset = 0, CancellationToken ct = default);
}

/// <summary>心跳快照。</summary>
/// <param name="Spec">槽位深拷贝；null 表示尚未配置。</param>
/// <param name="Status">Quartz 运行时状态；未配置时为 null。</param>
/// <param name="LastRun">最近一次执行记录；从未执行时为 null。</param>
/// <param name="SchedulerStarted">调度器是否已启动。</param>
public sealed record HeartbeatSnapshot(
    ScheduledJobSpec? Spec,
    JobStatus? Status,
    JobExecutionRecord? LastRun,
    bool SchedulerStarted)
{
    /// <summary>是否已配置。</summary>
    public bool Configured => Spec is not null;

    /// <summary>是否启用（已配置且 intent 为 Active）。</summary>
    public bool Enabled => Spec?.Intent == ScheduleIntent.Active;
}

/// <summary>心跳局部更新。未出现的字段保持不变。</summary>
public sealed record HeartbeatUpdate
{
    public bool? Enabled { get; init; }
    public string? Prompt { get; init; }
    public string? Every { get; init; }
    public IReadOnlyList<ScheduleWindow>? Windows { get; init; }
    public string? Timezone { get; init; }
    public string? SessionId { get; init; }
    public ConversationSpec? Conversation { get; init; }
    public string? Agent { get; init; }
    public string? Model { get; init; }
    public int? TimeoutSeconds { get; init; }
}
