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
    
    // 追踪正在执行的任务
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunInfo> _runningJobs = new();

    public event EventHandler<JobStatusChangedEventArgs>? JobStatusChanged;
    public event EventHandler<JobProgressEventArgs>? JobProgress;

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
            
            // 迁移旧版本数据
            _jobsFile.MigrateIfNeeded();

            // 启动 Quartz 调度器
            await _engine.StartAsync(ct);

            // 注册用户任务（只注册 Active 状态的）
            foreach (var job in _jobsFile.Jobs.Where(j => j.Intent == ScheduleIntent.Active))
                await RegisterUserJobAsync(job, ct);

            // 注册心跳
            await RegisterHeartbeatAsync(ct);
            
            // 恢复 Running 状态（从 Quartz 获取正在执行的任务）
            if (_engine.Scheduler != null)
            {
                var executingJobs = await _engine.Scheduler.GetCurrentlyExecutingJobs(ct);
                foreach (var ctx in executingJobs)
                {
                    var jobId = ctx.JobDetail.Key.Name;
                    var runId = ctx.MergedJobDataMap.GetStringValue(JobDataKeys.RunId) ?? Guid.NewGuid().ToString("N");
                    _runningJobs.TryAdd(jobId, new RunInfo(runId, ctx.FireTimeUtc.LocalDateTime));
                }
            }

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
            _runningJobs.Clear();
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
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null)
        {
            return new JobStatus { JobId = jobId, State = JobState.Disabled };
        }
        
        // 计算状态：Intent + Running
        var state = ComputeState(job);
        
        // 从 Quartz 获取调度信息
        var engineStatus = await _engine.GetJobStatusAsync(jobId, ct);
        
        // 从执行记录获取最后执行信息
        var lastRecord = await _repository.GetHistoryAsync(jobId, 1, 0, ct);
        var last = lastRecord.FirstOrDefault();
        
        return new JobStatus
        {
            JobId = jobId,
            JobName = job.Name,
            State = state,
            PreviousFireTime = engineStatus.PreviousFireTime,
            NextFireTime = engineStatus.NextFireTime,
            TriggerType = engineStatus.TriggerType,
            CronExpression = engineStatus.CronExpression,
            LastRunId = last?.RunId,
            LastExecutionTime = last?.StartedAt,
            LastExecutionSuccess = last?.Status == "success",
            LastError = last?.Status == "failed" ? last.Error : null
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobStatus>> GetAllJobStatusesAsync(CancellationToken ct = default)
    {
        var result = new List<JobStatus>();
        foreach (var job in _jobsFile.Jobs)
        {
            result.Add(await GetJobStatusAsync(job.Id, ct));
        }
        return result;
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

        if (_started && job.Intent == ScheduleIntent.Active)
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
    public async Task<TriggerResult> RunJobOnceAsync(string jobId, CancellationToken ct = default)
    {
        // Heartbeat 是特殊任务，不在 _jobsFile.Jobs 中
        if (jobId == SchedulerConstants.HeartbeatJobId)
        {
            var runId = Guid.NewGuid().ToString("N");
            if (!_runningJobs.TryAdd(jobId, new RunInfo(runId, DateTime.Now)))
                return new TriggerResult.Conflict("任务正在执行中");
            
            try
            {
                // 确保 Heartbeat 已注册
                var engineStatus = await _engine.GetJobStatusAsync(jobId, ct);
                if (engineStatus.State == JobState.Disabled)
                {
                    await RegisterHeartbeatAsync(ct);
                }
                
                var additionalData = new JobDataMap { [JobDataKeys.Source] = ScheduleSources.Manual };
                await _engine.TriggerJobAsync(jobId, runId, additionalData, ct);
                
                _logger.LogInformation("Heartbeat triggered with RunId={RunId}", runId);
                return new TriggerResult.Accepted(runId);
            }
            catch
            {
                _runningJobs.TryRemove(jobId, out _);
                throw;
            }
        }
        
        // 普通任务
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return new TriggerResult.NotFound();
        if (job.Intent == ScheduleIntent.Disabled) return new TriggerResult.Disabled();
        
        // 检查是否正在执行
        var runId2 = Guid.NewGuid().ToString("N");
        if (!_runningJobs.TryAdd(jobId, new RunInfo(runId2, DateTime.Now)))
            return new TriggerResult.Conflict("任务正在执行中");
        
        try
        {
            // 确保 Job 已注册到 Quartz
            var engineStatus = await _engine.GetJobStatusAsync(jobId, ct);
            if (engineStatus.State == JobState.Disabled)
            {
                // Job 不存在于 Quartz，需要先注册
                _logger.LogDebug("Job {JobId} not found in Quartz, registering before trigger", jobId);
                await RegisterUserJobAsync(job, ct);
            }
            
            // 使用原生 TriggerJob
            var additionalData = new JobDataMap { [JobDataKeys.Source] = ScheduleSources.Manual };
            await _engine.TriggerJobAsync(jobId, runId2, additionalData, ct);
            
            _logger.LogInformation("Job {JobId} triggered with RunId={RunId}", jobId, runId2);
            return new TriggerResult.Accepted(runId2);
        }
        catch
        {
            _runningJobs.TryRemove(jobId, out _);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SetJobIntentAsync(string jobId, ScheduleIntent intent, CancellationToken ct = default)
    {
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null)
        {
            _logger.LogWarning("Attempted to set intent for non-existent job: {JobId}", jobId);
            return;
        }
        
        var oldIntent = job.Intent;
        job.Intent = intent;
        
        // 同步到 Quartz
        switch (intent)
        {
            case ScheduleIntent.Disabled:
                await _engine.RemoveJobAsync(jobId, ct);
                break;
            case ScheduleIntent.Paused:
                // 先确保任务存在，然后暂停
                if (oldIntent == ScheduleIntent.Disabled)
                    await RegisterUserJobAsync(job, ct);
                await _engine.PauseJobAsync(jobId, ct);
                break;
            case ScheduleIntent.Active:
                if (oldIntent == ScheduleIntent.Disabled)
                    await RegisterUserJobAsync(job, ct);
                else
                    await _engine.ResumeJobAsync(jobId, ct);
                break;
        }
        
        await _repository.SaveAsync(_jobsFile, ct);
        
        // 触发状态变更事件
        JobStatusChanged?.Invoke(this, new JobStatusChangedEventArgs
        {
            JobId = jobId,
            OldState = ComputeStateFromIntent(oldIntent),
            NewState = ComputeStateFromIntent(intent)
        });
        
        _logger.LogDebug("Job {JobId} intent changed: {OldIntent} -> {NewIntent}", jobId, oldIntent, intent);
    }

    /// <inheritdoc/>
    public async Task PauseJobAsync(string jobId, CancellationToken ct = default)
    {
        await SetJobIntentAsync(jobId, ScheduleIntent.Paused, ct);
    }

    /// <inheritdoc/>
    public async Task ResumeJobAsync(string jobId, CancellationToken ct = default)
    {
        await SetJobIntentAsync(jobId, ScheduleIntent.Active, ct);
    }

    /// <inheritdoc/>
    public async Task DisableJobAsync(string jobId, CancellationToken ct = default)
    {
        await SetJobIntentAsync(jobId, ScheduleIntent.Disabled, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        var result = await _engine.CancelJobAsync(jobId, ct);
        
        // 无论 Quartz 中断是否成功，都清理本地运行状态
        // 因为任务可能已经完成但状态未更新
        _runningJobs.TryRemove(jobId, out _);
        
        // 触发状态变更事件
        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job != null)
        {
            var jobState = ComputeState(job);
            JobStatusChanged?.Invoke(this, new JobStatusChangedEventArgs
            {
                JobId = jobId,
                OldState = JobState.Running,
                NewState = jobState
            });
        }
        
        return result;
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

    // ===== IJobExecutionListener =====

    /// <inheritdoc/>
    void IJobExecutionListener.OnJobStart(string jobId, string runId)
    {
        _runningJobs.AddOrUpdate(jobId, new RunInfo(runId, DateTime.Now), (_, _) => new RunInfo(runId, DateTime.Now));
        _logger.LogDebug("Job {JobId} started with RunId={RunId}", jobId, runId);
        
        // 触发进度事件
        JobProgress?.Invoke(this, new JobProgressEventArgs
        {
            JobId = jobId,
            RunId = runId,
            Stage = JobProgressStage.Triggered
        });
    }

    /// <inheritdoc/>
    public async Task OnJobExecutedAsync(string jobId, JobExecutionResult result, CancellationToken ct = default)
    {
        // 移除 Running 状态
        _runningJobs.TryRemove(jobId, out var runInfo);
        
        // 记录历史（包含执行快照）
        var record = new JobExecutionRecord
        {
            JobId = jobId,
            RunId = result.RunId ?? runInfo?.RunId ?? Guid.NewGuid().ToString("N"),
            Source = result.Source,
            StartedAt = runInfo?.StartTime ?? DateTime.Now,
            CompletedAt = DateTime.Now,
            Status = result.Success ? "success" : "failed",
            Output = result.Output,
            Error = result.Error,
            // 执行快照参数
            TaskType = result.TaskType,
            Agent = result.Agent,
            Prompt = result.Prompt,
            Text = result.Text,
            SessionId = result.SessionId,
            DispatchChannel = result.DispatchChannel,
            DispatchUserId = result.DispatchUserId,
            DispatchSessionId = result.DispatchSessionId
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
        var jobState = job != null ? ComputeState(job) : JobState.Disabled;
        JobStatusChanged?.Invoke(this, new JobStatusChangedEventArgs
        {
            JobId = jobId,
            NewState = jobState,
            Error = result.Error
        });

        // 触发进度事件
        JobProgress?.Invoke(this, new JobProgressEventArgs
        {
            JobId = jobId,
            RunId = record.RunId,
            Stage = result.Success ? JobProgressStage.Completed : JobProgressStage.Failed,
            Result = result
        });

        _logger.LogDebug("Job {JobId} executed: {Status}", jobId, record.Status);
    }

    // ===== Private Methods =====

    private JobState ComputeState(ScheduledJobSpec job)
    {
        // 如果正在执行，返回 Running
        if (_runningJobs.ContainsKey(job.Id))
            return JobState.Running;
        
        // 否则根据 Intent 返回状态
        return job.Intent switch
        {
            ScheduleIntent.Disabled => JobState.Disabled,
            ScheduleIntent.Paused => JobState.Paused,
            ScheduleIntent.Active => JobState.Scheduled,
            _ => JobState.Disabled
        };
    }
    
    private static JobState ComputeStateFromIntent(ScheduleIntent intent) => intent switch
    {
        ScheduleIntent.Disabled => JobState.Disabled,
        ScheduleIntent.Paused => JobState.Paused,
        ScheduleIntent.Active => JobState.Scheduled,
        _ => JobState.Disabled
    };

    private async Task RegisterUserJobAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        var jobData = new JobDataMap
        {
            [JobDataKeys.JobId] = job.Id,
            [JobDataKeys.JobName] = job.Name ?? job.Id,
            [JobDataKeys.TaskType] = job.TaskType,
            [JobDataKeys.SessionId] = job.Dispatch.Target.SessionId ?? "main",
            [JobDataKeys.TimeoutSeconds] = job.Runtime.TimeoutSeconds.ToString()
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

        await _engine.UpsertJobAsync(job.Id, job.Schedule, job.Intent, jobData, ct);
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
            [JobDataKeys.TimeoutSeconds] = hb.TimeoutSeconds.ToString(),
            [JobDataKeys.AgentId] = hb.Agent ?? string.Empty,
            [JobDataKeys.Prompt] = hb.Prompt ?? string.Empty,
            [JobDataKeys.HeartbeatTarget] = hb.Target,
            [JobDataKeys.Source] = ScheduleSources.Heartbeat
        };

        if (hb.ActiveHours != null)
        {
            jobData[JobDataKeys.ActiveHours] = System.Text.Json.JsonSerializer.Serialize(hb.ActiveHours);
        }

        await _engine.UpsertJobAsync(SchedulerConstants.HeartbeatJobId, schedule, ScheduleIntent.Active, jobData, ct);
    }
}
