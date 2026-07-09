using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>任务执行通知服务</summary>
public sealed class JobNotificationService
{
    private readonly ILogger<JobNotificationService> _logger;
    
    /// <summary>任务错误事件</summary>
    public event EventHandler<JobErrorEventArgs>? JobError;
    
    /// <summary>任务成功事件</summary>
    public event EventHandler<JobSuccessEventArgs>? JobSuccess;
    
    /// <summary>任务进度事件</summary>
    public event EventHandler<JobProgressEventArgs>? JobProgress;

    public JobNotificationService(
        ILogger<JobNotificationService> logger,
        SchedulerStatusService statusService)
    {
        _logger = logger;
        
        // 订阅 SchedulerStatusService 的进度事件，建立事件链路
        statusService.JobProgress += (_, args) => HandleProgress(args);
    }

    /// <summary>通知任务错误</summary>
    public void NotifyError(string jobId, string error, string? output = null)
    {
        _logger.LogWarning("Job {JobId} error: {Error}", jobId, error);
        
        JobError?.Invoke(this, new JobErrorEventArgs
        {
            JobId = jobId,
            Error = error,
            Output = output,
            Timestamp = DateTime.Now
        });
    }

    /// <summary>通知任务成功</summary>
    public void NotifySuccess(string jobId, string? output = null)
    {
        _logger.LogDebug("Job {JobId} completed successfully", jobId);
        
        JobSuccess?.Invoke(this, new JobSuccessEventArgs
        {
            JobId = jobId,
            Output = output,
            Timestamp = DateTime.Now
        });
    }
    
    /// <summary>通知任务进度</summary>
    public void NotifyProgress(JobProgressEventArgs args)
    {
        _logger.LogDebug("Job {JobId} progress: {Stage}", args.JobId, args.Stage);
        JobProgress?.Invoke(this, args);
    }

    /// <summary>处理任务执行结果</summary>
    public Task HandleResultAsync(string jobId, JobExecutionResult result)
    {
        if (result.Success)
        {
            NotifySuccess(jobId, result.Output);
        }
        else
        {
            NotifyError(jobId, result.Error ?? "Unknown error", result.Output);
        }
        return Task.CompletedTask;
    }
    
    /// <summary>处理进度事件</summary>
    public void HandleProgress(JobProgressEventArgs args)
    {
        NotifyProgress(args);
        
        // 兼容旧的事件系统
        if (args.Stage == JobProgressStage.Completed && args.Result != null)
        {
            _ = HandleResultAsync(args.JobId, args.Result);
        }
        else if (args.Stage == JobProgressStage.Failed && args.Result != null)
        {
            _ = HandleResultAsync(args.JobId, args.Result);
        }
    }
}

/// <summary>任务错误事件参数</summary>
public sealed class JobErrorEventArgs : EventArgs
{
    public string JobId { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string? Output { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>任务成功事件参数</summary>
public sealed class JobSuccessEventArgs : EventArgs
{
    public string JobId { get; init; } = string.Empty;
    public string? Output { get; init; }
    public DateTime Timestamp { get; init; }
}