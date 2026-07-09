using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Abstractions;

/// <summary>任务持久化抽象</summary>
public interface IScheduleRepository
{
    Task<JobsFile> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(JobsFile jobs, CancellationToken ct = default);
    Task AppendHistoryAsync(string jobId, JobExecutionRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(string jobId, int limit, CancellationToken ct = default);
}

/// <summary>结果投递抽象</summary>
public interface IScheduledJobDispatcher
{
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default);
}

/// <summary>任务执行监听器</summary>
public interface IJobExecutionListener
{
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
    Task<JobExecutionResult> RunJobOnceAsync(string jobId, CancellationToken ct = default);
    Task PauseJobAsync(string jobId, CancellationToken ct = default);
    Task ResumeJobAsync(string jobId, CancellationToken ct = default);
    
    // ===== 心跳 =====
    Task ReloadHeartbeatAsync(CancellationToken ct = default);
}
