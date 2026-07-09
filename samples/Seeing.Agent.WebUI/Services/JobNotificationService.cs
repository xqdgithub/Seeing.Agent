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

    public JobNotificationService(ILogger<JobNotificationService> logger)
    {
        _logger = logger;
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