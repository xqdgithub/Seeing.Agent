using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Management;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>调度器状态服务 - 为 WebUI 提供状态查询</summary>
public sealed class SchedulerStatusService
{
    private readonly IScheduleManager _scheduleManager;
    private readonly ILogger<SchedulerStatusService> _logger;
    
    /// <summary>任务状态变更事件</summary>
    public event EventHandler<JobStatusChangedEventArgs>? JobStatusChanged;
    
    /// <summary>任务进度事件</summary>
    public event EventHandler<JobProgressEventArgs>? JobProgress;

    public SchedulerStatusService(
        IScheduleManager scheduleManager,
        ILogger<SchedulerStatusService> logger)
    {
        _scheduleManager = scheduleManager;
        _logger = logger;

        // 订阅 ScheduleManager 的事件
        if (_scheduleManager is ScheduleManager manager)
        {
            manager.JobStatusChanged += (_, args) => JobStatusChanged?.Invoke(this, args);
            manager.JobProgress += (_, args) => JobProgress?.Invoke(this, args);
        }
    }

    /// <summary>获取调度器状态</summary>
    public async Task<SchedulerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _scheduleManager.GetStatusAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduler status");
            return new SchedulerStatus { IsRunning = false };
        }
    }

    /// <summary>获取所有任务状态</summary>
    public async Task<IReadOnlyList<JobStatus>> GetAllJobStatusesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _scheduleManager.GetAllJobStatusesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job statuses");
            return Array.Empty<JobStatus>();
        }
    }

    /// <summary>获取指定任务状态</summary>
    public async Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            return await _scheduleManager.GetJobStatusAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job status for {JobId}", jobId);
            return new JobStatus { JobId = jobId, State = JobState.Disabled };
        }
    }

    /// <summary>启动调度器</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _scheduleManager.StartAsync(ct);
    }

    /// <summary>停止调度器</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _scheduleManager.StopAsync(ct);
    }

    /// <summary>暂停任务</summary>
    public async Task PauseJobAsync(string jobId, CancellationToken ct = default)
    {
        await _scheduleManager.PauseJobAsync(jobId, ct);
    }

    /// <summary>恢复任务</summary>
    public async Task ResumeJobAsync(string jobId, CancellationToken ct = default)
    {
        await _scheduleManager.ResumeJobAsync(jobId, ct);
    }

    /// <summary>禁用任务</summary>
    public async Task DisableJobAsync(string jobId, CancellationToken ct = default)
    {
        await _scheduleManager.DisableJobAsync(jobId, ct);
    }

    /// <summary>取消正在运行的任务</summary>
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        return await _scheduleManager.CancelJobAsync(jobId, ct);
    }
}