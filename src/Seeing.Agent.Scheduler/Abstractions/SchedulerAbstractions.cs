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

/// <summary>任务执行抽象</summary>
public interface IScheduledJobExecutor
{
    Task<JobExecutionResult> ExecuteAsync(ScheduledJobSpec job, CancellationToken ct = default);
}

/// <summary>结果投递抽象</summary>
public interface IScheduledJobDispatcher
{
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default);
}

/// <summary>心跳执行抽象</summary>
public interface IHeartbeatRunner
{
    Task<JobExecutionResult> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>调度管理抽象</summary>
public interface IScheduleManager
{
    bool IsStarted { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJobSpec>> ListJobsAsync(CancellationToken ct = default);
    Task<ScheduledJobSpec?> GetJobAsync(string jobId, CancellationToken ct = default);
    Task<ScheduledJobSpec> CreateOrReplaceJobAsync(ScheduledJobSpec job, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default);
    Task<JobExecutionResult> RunJobOnceAsync(string jobId, CancellationToken ct = default);
    Task ReloadHeartbeatAsync(CancellationToken ct = default);
}
