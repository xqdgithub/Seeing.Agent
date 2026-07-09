using Microsoft.Extensions.Logging;
using Quartz;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Management;

/// <summary>调度管理器 — Quartz 包装，提供 CRUD、状态查询、生命周期管理</summary>
public sealed class ScheduleManager : IScheduleManager, IJobExecutionListener
{
    private readonly IScheduleRepository _repository;
    private readonly QuartzSchedulerEngine _engine;
    private readonly ISchedulerOptionsProvider _optionsProvider;
    private readonly ILogger<ScheduleManager> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private JobsFile _jobsFile = new();
    private bool _started;

    public event EventHandler<JobStatusChangedEventArgs>? JobStatusChanged;

    public ScheduleManager(
        IScheduleRepository repository,
        QuartzSchedulerEngine engine,
        ISchedulerOptionsProvider optionsProvider,
        ILogger<ScheduleManager> logger)
    {
        _repository = repository;
        _engine = engine;
        _optionsProvider = optionsProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsStarted => _started;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_started)
                return;

            _optionsProvider.Reload();
            var options = _optionsProvider.Current;
            if (!options.Enabled)
            {
                _logger.LogDebug("Scheduler disabled, skipping start");
                return;
            }

            // 加载任务文件
            _jobsFile = await _repository.LoadAsync(ct);

            // 启动 Quartz 调度器
            await _engine.StartAsync(ct);

            // 注册用户任务
            foreach (var job in _jobsFile.Jobs.Where(j => j.Enabled))
                await RegisterUserJobAsync(job, ct);

            // 注册心跳
            await RegisterHeartbeatAsync(ct);

            _started = true;
            _logger.LogInformation("ScheduleManager started with {Count} user jobs", _jobsFile.Jobs.Count);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_started)
                return;

            await _engine.StopAsync(ct);
            _started = false;
            _logger.LogInformation("ScheduleManager stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<SchedulerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        return await _engine.GetStatusAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        return await _engine.GetJobStatusAsync(jobId, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobStatus>> GetAllJobStatusesAsync(CancellationToken ct = default)
    {
        return await _engine.GetAllJobStatusesAsync(ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ScheduledJobSpec>> ListJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScheduledJobSpec>>(_jobsFile.Jobs.ToList());
    }

    /// <inheritdoc/>
    public Task<ScheduledJobSpec?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId));
    }

    /// <inheritdoc/>
    public async Task<ScheduledJobSpec> CreateOrReplaceJobAsync(ScheduledJobSpec job, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N");

        var existing = _jobsFile.Jobs.FindIndex(j => j.Id == job.Id);
        if (existing >= 0)
            _jobsFile.Jobs[existing] = job;
        else
            _jobsFile.Jobs.Add(job);

        if (_started)
            await RegisterUserJobAsync(job, ct);

        await _repository.SaveAsync(_jobsFile, ct);

        return job;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default)
    {
        var removed = _jobsFile.Jobs.RemoveAll(j => j.Id == jobId) > 0;
        if (!removed)
            return false;

        await _repository.SaveAsync(_jobsFile, ct);
        await _engine.RemoveJobAsync(jobId, ct);
        return true;
    }

    /// <inheritdoc/>
    public async Task<JobExecutionResult> RunJobOnceAsync(string jobId, CancellationToken ct = default)
    {
        // 检查 Job 是否已注册到 Quartz（可能因应用重启或 Job 被禁用而丢失）
        var jobStatus = await _engine.GetJobStatusAsync(jobId, ct);
        if (jobStatus.State == JobState.Completed)
        {
            // Job 不存在于 Quartz，需要先注册
            _logger.LogDebug("Job {JobId} not found in Quartz, registering before trigger", jobId);
            
            if (jobId == SchedulerConstants.HeartbeatJobId)
            {
                await RegisterHeartbeatAsync(ct);
            }
            else
            {
                var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId)
                    ?? throw new InvalidOperationException($"Job '{jobId}' not found");
                await RegisterUserJobAsync(job, ct);
            }
        }

        // 手动触发
        await _engine.TriggerJobAsync(jobId, ct);

        // 返回结果
        if (jobId == SchedulerConstants.HeartbeatJobId)
        {
            return new JobExecutionResult
            {
                Success = true,
                Output = "Heartbeat triggered",
                Source = ScheduleSources.Heartbeat
            };
        }
        else
        {
            var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
            return new JobExecutionResult
            {
                Success = true,
                Output = "Job triggered",
                Source = ScheduleSources.Cron,
                SessionId = job?.Dispatch.Target.SessionId ?? "main"
            };
        }
    }

    /// <inheritdoc/>
    public async Task PauseJobAsync(string jobId, CancellationToken ct = default)
    {
        await _engine.PauseJobAsync(jobId, ct);
        
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job != null)
        {
            job.Enabled = false;
            await _repository.SaveAsync(_jobsFile, ct);
        }
    }

    /// <inheritdoc/>
    public async Task ResumeJobAsync(string jobId, CancellationToken ct = default)
    {
        await _engine.ResumeJobAsync(jobId, ct);
        
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job != null)
        {
            job.Enabled = true;
            await _repository.SaveAsync(_jobsFile, ct);
        }
    }

    /// <inheritdoc/>
    public async Task ReloadHeartbeatAsync(CancellationToken ct = default)
    {
        _optionsProvider.Reload();
        if (!_started)
            return;

        await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct);
        await RegisterHeartbeatAsync(ct);
    }

    /// <inheritdoc/>
    public async Task OnJobExecutedAsync(string jobId, JobExecutionResult result, CancellationToken ct = default)
    {
        // 记录历史
        var record = new JobExecutionRecord
        {
            Source = result.Source,
            StartedAt = DateTime.Now,
            CompletedAt = DateTime.Now,
            Status = result.Success ? "success" : "failed",
            Output = result.Output,
            Error = result.Error
        };

        if (!jobId.StartsWith('_'))
            await _repository.AppendHistoryAsync(jobId, record, ct);

        // 更新任务最后运行时间
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job != null)
        {
            job.LastRunAt = record.StartedAt;
            await _repository.SaveAsync(_jobsFile, ct);
        }

        // 触发状态变更事件
        var oldState = result.Success ? JobState.Normal : JobState.Error;
        var newState = result.Success ? JobState.Normal : JobState.Error;
        
        JobStatusChanged?.Invoke(this, new JobStatusChangedEventArgs
        {
            JobId = jobId,
            OldState = oldState,
            NewState = newState,
            Error = result.Error
        });

        _logger.LogDebug("Job {JobId} executed: {Status}", jobId, record.Status);
    }

    private async Task RegisterUserJobAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        var jobData = new JobDataMap
        {
            [JobDataKeys.JobId] = job.Id,
            [JobDataKeys.JobName] = job.Name ?? job.Id,
            [JobDataKeys.TaskType] = job.TaskType,
            [JobDataKeys.SessionId] = job.Dispatch.Target.SessionId ?? "main",
            [JobDataKeys.TimeoutSeconds] = job.Runtime.TimeoutSeconds
        };

        if (job.TaskType == ScheduleTaskTypes.Text)
        {
            jobData[JobDataKeys.Text] = job.Text ?? string.Empty;
        }
        else
        {
            jobData[JobDataKeys.AgentId] = job.Agent ?? string.Empty;
            jobData[JobDataKeys.Prompt] = job.Prompt ?? string.Empty;
        }

        // 投递配置
        if (!string.IsNullOrEmpty(job.Dispatch.Target.Channel))
            jobData[JobDataKeys.DispatchChannel] = job.Dispatch.Target.Channel;
        if (!string.IsNullOrEmpty(job.Dispatch.Target.UserId))
            jobData[JobDataKeys.DispatchUserId] = job.Dispatch.Target.UserId;
        if (!string.IsNullOrEmpty(job.Dispatch.Target.SessionId))
            jobData[JobDataKeys.DispatchSessionId] = job.Dispatch.Target.SessionId;

        await _engine.UpsertJobAsync(job.Id, job.Schedule, job.Enabled, jobData, ct);
    }

    private async Task RegisterHeartbeatAsync(CancellationToken ct)
    {
        var hb = _optionsProvider.Current.Heartbeat;
        if (!hb.Enabled)
            return;

        var schedule = new ScheduleSpec
        {
            Type = ScheduleTypes.Interval,
            Every = hb.Every,
            Timezone = _optionsProvider.Current.Timezone
        };

        var jobData = new JobDataMap
        {
            [JobDataKeys.JobId] = SchedulerConstants.HeartbeatJobId,
            [JobDataKeys.SessionId] = hb.SessionId,
            [JobDataKeys.TimeoutSeconds] = hb.TimeoutSeconds,
            [JobDataKeys.AgentId] = hb.Agent ?? string.Empty,
            [JobDataKeys.QueryFile] = hb.QueryFile,
            [JobDataKeys.HeartbeatTarget] = hb.Target,
            [JobDataKeys.Source] = ScheduleSources.Heartbeat
        };

        if (hb.ActiveHours != null)
        {
            jobData[JobDataKeys.ActiveHours] = System.Text.Json.JsonSerializer.Serialize(hb.ActiveHours);
        }

        await _engine.UpsertJobAsync(SchedulerConstants.HeartbeatJobId, schedule, true, jobData, ct);
    }
}