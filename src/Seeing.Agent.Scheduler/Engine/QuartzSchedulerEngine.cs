using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>Quartz.NET 调度引擎包装</summary>
public sealed class QuartzSchedulerEngine : IAsyncDisposable
{
    private readonly ILogger<QuartzSchedulerEngine> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private IScheduler? _scheduler;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public QuartzSchedulerEngine(
        ISchedulerFactory schedulerFactory,
        ILogger<QuartzSchedulerEngine> logger)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    /// <summary>调度器实例</summary>
    public IScheduler? Scheduler => _scheduler;

    /// <summary>是否已启动</summary>
    public bool IsStarted => _scheduler?.IsStarted ?? false;

    /// <summary>是否在待机模式</summary>
    public bool IsStandby => _scheduler?.InStandbyMode ?? false;

    /// <summary>获取调度器状态</summary>
    public async Task<SchedulerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null)
            {
                return new SchedulerStatus { IsRunning = false, IsStarted = false };
            }

            var metaData = await _scheduler.GetMetaData(ct);
            var jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);
            var runningJobs = await _scheduler.GetCurrentlyExecutingJobs(ct);

            // 正确计算 PausedJobs：遍历所有 job 的 trigger 状态
            var pausedCount = 0;
            foreach (var jobKey in jobKeys)
            {
                var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct);
                foreach (var trigger in triggers)
                {
                    var state = await _scheduler.GetTriggerState(trigger.Key, ct);
                    if (state == TriggerState.Paused)
                    {
                        pausedCount++;
                        break; // 一个 job 只计一次
                    }
                }
            }

            return new SchedulerStatus
            {
                IsRunning = !metaData.InStandbyMode && metaData.Started,
                IsStarted = metaData.Started,
                IsStandby = metaData.InStandbyMode,
                TotalJobs = jobKeys.Count,
                RunningJobs = runningJobs.Count,
                PausedJobs = pausedCount,
                StartTime = metaData.RunningSince?.LocalDateTime,  // 使用 Quartz 的实际启动时间
                ThreadPoolSize = metaData.ThreadPoolSize
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>获取任务状态</summary>
    public async Task<JobStatus> GetJobStatusAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null)
            {
                return new JobStatus { JobId = jobId, State = JobState.Paused };
            }

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            var jobDetail = await _scheduler.GetJobDetail(jobKey, ct);
            
            if (jobDetail == null)
            {
                return new JobStatus { JobId = jobId, State = JobState.Completed };
            }

            var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct);
            var trigger = triggers.FirstOrDefault();

            var state = DetermineJobState(trigger, await _scheduler.GetTriggerState(trigger?.Key!, ct));

            // Quartz 返回 UTC，转为本地 DateTime
            return new JobStatus
            {
                JobId = jobId,
                JobName = jobDetail.Description ?? jobId,
                JobGroup = jobKey.Group,
                State = state,
                PreviousFireTime = trigger?.GetPreviousFireTimeUtc()?.LocalDateTime,
                NextFireTime = trigger?.GetNextFireTimeUtc()?.LocalDateTime,
                TriggerType = trigger?.GetType().Name,
                CronExpression = trigger is ICronTrigger cronTrigger ? cronTrigger.CronExpressionString : null
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>获取所有任务状态</summary>
    public async Task<IReadOnlyList<JobStatus>> GetAllJobStatusesAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null)
                return Array.Empty<JobStatus>();

            var jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);
            var statuses = new List<JobStatus>();

            foreach (var jobKey in jobKeys)
            {
                var status = await GetJobStatusAsyncInternal(jobKey.Name, ct);
                if (status != null)
                    statuses.Add(status);
            }

            return statuses;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JobStatus?> GetJobStatusAsyncInternal(string jobId, CancellationToken ct)
    {
        if (_scheduler == null) return null;

        var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
        var jobDetail = await _scheduler.GetJobDetail(jobKey, ct);

        if (jobDetail == null) return null;

        var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct);
        var trigger = triggers.FirstOrDefault();
        var triggerState = trigger != null 
            ? await _scheduler.GetTriggerState(trigger.Key, ct) 
            : TriggerState.None;

        // Quartz 返回 UTC，转为本地 DateTime
        return new JobStatus
        {
            JobId = jobId,
            JobName = jobDetail.Description ?? jobId,
            JobGroup = jobKey.Group,
            State = DetermineJobState(trigger, triggerState),
            PreviousFireTime = trigger?.GetPreviousFireTimeUtc()?.LocalDateTime,
            NextFireTime = trigger?.GetNextFireTimeUtc()?.LocalDateTime,
            TriggerType = trigger?.GetType().Name,
            CronExpression = trigger is ICronTrigger cronTrigger ? cronTrigger.CronExpressionString : null
        };
    }

    private static JobState DetermineJobState(ITrigger? trigger, TriggerState triggerState)
    {
        return triggerState switch
        {
            TriggerState.Normal => JobState.Normal,
            TriggerState.Paused => JobState.Paused,
            TriggerState.Complete => JobState.Completed,
            TriggerState.Error => JobState.Error,
            TriggerState.Blocked => JobState.Blocked,
            _ => JobState.Normal
        };
    }

    /// <summary>启动调度器</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null)
            {
                _scheduler = await _schedulerFactory.GetScheduler(ct);
            }

            if (!_scheduler.IsStarted)
            {
                await _scheduler.Start(ct);
                _logger.LogInformation("Quartz scheduler started");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>停止调度器</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler != null && _scheduler.IsStarted)
            {
                await _scheduler.Shutdown(true, ct);
                _logger.LogInformation("Quartz scheduler stopped");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>待机模式</summary>
    public async Task StandbyAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler != null && !_scheduler.InStandbyMode)
            {
                await _scheduler.Standby(ct);
                _logger.LogInformation("Quartz scheduler in standby mode");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>创建或更新任务</summary>
    public async Task UpsertJobAsync(
        string jobId,
        ScheduleSpec schedule,
        bool enabled,
        JobDataMap? jobData = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null)
            {
                _scheduler = await _schedulerFactory.GetScheduler(ct);
            }

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);

            // 删除现有任务
            if (await _scheduler.CheckExists(jobKey, ct))
            {
                await _scheduler.DeleteJob(jobKey, ct);
            }

            if (!enabled)
            {
                _logger.LogDebug("Job {JobId} disabled, skipping creation", jobId);
                return;
            }

            // 创建 JobDetail
            var jobType = jobId == SchedulerConstants.HeartbeatJobId 
                ? typeof(Jobs.HeartbeatJob) 
                : typeof(Jobs.AgentJob);

            var jobBuilder = JobBuilder.Create(jobType)
                .WithIdentity(jobKey)
                .WithDescription(jobId)
                .StoreDurably();

            if (jobData != null)
            {
                jobBuilder.UsingJobData(jobData);
            }

            var jobDetail = jobBuilder.Build();

            // 创建触发器
            var trigger = BuildTrigger(jobId, schedule);

            // 调度任务
            await _scheduler.ScheduleJob(jobDetail, trigger, ct);
            _logger.LogDebug("Job {JobId} scheduled with trigger {TriggerType}", jobId, trigger.GetType().Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>删除任务</summary>
    public async Task RemoveJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            if (await _scheduler.CheckExists(jobKey, ct))
            {
                await _scheduler.DeleteJob(jobKey, ct);
                _logger.LogDebug("Job {JobId} removed", jobId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>暂停任务</summary>
    public async Task PauseJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            await _scheduler.PauseJob(jobKey, ct);
            _logger.LogDebug("Job {JobId} paused", jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>恢复任务</summary>
    public async Task ResumeJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            await _scheduler.ResumeJob(jobKey, ct);
            _logger.LogDebug("Job {JobId} resumed", jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>立即触发任务</summary>
    public async Task TriggerJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            await _scheduler.TriggerJob(jobKey, ct);
            _logger.LogDebug("Job {JobId} triggered manually", jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>构建触发器</summary>
    private ITrigger BuildTrigger(string jobId, ScheduleSpec schedule)
    {
        var triggerKey = new TriggerKey(jobId + "_trigger", SchedulerConstants.DefaultTriggerGroup);
        var timezone = ScheduleExpressionParser.ResolveTimeZone(schedule.Timezone);

        var triggerBuilder = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobId, SchedulerConstants.DefaultJobGroup)
            .StartNow();

        switch (schedule.Type)
        {
            case ScheduleTypes.Cron:
                if (string.IsNullOrEmpty(schedule.Cron))
                    throw new InvalidOperationException("Cron schedule requires 'cron' expression");

                // 使用统一的 NormalizeCron
                var cron = ScheduleExpressionParser.NormalizeCron(schedule.Cron);
                triggerBuilder.WithCronSchedule(cron, x => x.InTimeZone(timezone));
                break;

            case ScheduleTypes.Interval:
                if (string.IsNullOrEmpty(schedule.Every))
                    throw new InvalidOperationException("Interval schedule requires 'every'");

                // 使用统一的 ParseInterval
                var interval = ScheduleExpressionParser.ParseInterval(schedule.Every);
                triggerBuilder.WithSimpleSchedule(x => x
                    .WithInterval(interval)
                    .RepeatForever());
                break;

            case ScheduleTypes.Once:
                if (schedule.RunAt == null)
                    throw new InvalidOperationException("Once schedule requires 'runAt'");

                // RunAt 是本地 DateTime，转为 UTC 给 Quartz
                triggerBuilder.StartAt(schedule.RunAt.Value.ToUniversalTime());
                triggerBuilder.WithSimpleSchedule(x => x.WithRepeatCount(0));
                break;

            default:
                throw new NotSupportedException($"Unsupported schedule type: {schedule.Type}");
        }

        return triggerBuilder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_scheduler != null)
            {
                await _scheduler.Shutdown(true);
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}