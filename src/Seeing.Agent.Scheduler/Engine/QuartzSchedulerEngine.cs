using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.Matchers;
using Seeing.Agent.Scheduler.Jobs;
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
            if (_scheduler == null || _scheduler.IsShutdown)
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
        catch (SchedulerException)
        {
            // Scheduler 已关闭或不可用
            return new SchedulerStatus { IsRunning = false, IsStarted = false };
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
                _logger.LogDebug("GetJobStatus({JobId}): scheduler is null, returning Disabled", jobId);
                return new JobStatus { JobId = jobId, State = JobState.Disabled };
            }

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            var jobDetail = await _scheduler.GetJobDetail(jobKey, ct);
            
            if (jobDetail == null)
            {
                _logger.LogDebug("GetJobStatus({JobId}): jobDetail is null, returning Disabled", jobId);
                return new JobStatus { JobId = jobId, State = JobState.Disabled };
            }

            var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct);
            
            // 只看主 trigger，忽略临时 trigger（MT_xxx, fire_xxx）
            var primaryTrigger = GetPrimaryTrigger(triggers, jobId);
            var triggerState = primaryTrigger != null 
                ? await _scheduler.GetTriggerState(primaryTrigger.Key, ct) 
                : TriggerState.None;

            // 检查是否正在执行
            var executingJobs = await _scheduler.GetCurrentlyExecutingJobs(ct);
            var isExecuting = executingJobs.Any(j => j.JobDetail.Key.Equals(jobKey));
            
            var state = isExecuting ? JobState.Running : DetermineJobState(primaryTrigger, triggerState);

            _logger.LogDebug("GetJobStatus({JobId}): trigger={T}, triggerState={S}, state={State}", 
                jobId, primaryTrigger?.Key?.Name ?? "null", triggerState, state);

            var tz = ResolveJobTimezone(jobDetail);
            return new JobStatus
            {
                JobId = jobId,
                JobName = jobDetail.Description ?? jobId,
                JobGroup = jobKey.Group,
                State = state,
                PreviousFireTime = ToScheduleLocal(GetLatestPreviousFireUtc(triggers, jobId), tz),
                NextFireTime = ToScheduleLocal(GetEarliestNextFireUtc(triggers, jobId), tz),
                TriggerType = primaryTrigger?.GetType().Name,
                CronExpression = primaryTrigger is ICronTrigger cronTrigger ? cronTrigger.CronExpressionString : null
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
            
            // 批量获取正在执行的任务，避免每个 job 单独查询
            var executingJobs = await _scheduler.GetCurrentlyExecutingJobs(ct);
            var executingJobKeys = new HashSet<JobKey>(executingJobs.Select(j => j.JobDetail.Key));
            
            var statuses = new List<JobStatus>();

            foreach (var jobKey in jobKeys)
            {
                var status = await GetJobStatusAsyncInternal(jobKey, executingJobKeys, ct);
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

    private async Task<JobStatus?> GetJobStatusAsyncInternal(JobKey jobKey, HashSet<JobKey>? executingJobKeys = null, CancellationToken ct = default)
    {
        if (_scheduler == null) return null;

        var jobDetail = await _scheduler.GetJobDetail(jobKey, ct);

        if (jobDetail == null) return null;

        // 使用传入的执行中任务集合，或重新查询
        var isExecuting = executingJobKeys?.Contains(jobKey) ?? 
            (await _scheduler.GetCurrentlyExecutingJobs(ct)).Any(j => j.JobDetail.Key.Equals(jobKey));

        var triggers = await _scheduler.GetTriggersOfJob(jobKey, ct);
        
        // 使用统一的 GetPrimaryTrigger 方法
        var primaryTrigger = GetPrimaryTrigger(triggers, jobKey.Name);
        
        var triggerState = primaryTrigger != null 
            ? await _scheduler.GetTriggerState(primaryTrigger.Key, ct) 
            : TriggerState.None;

        // 如果正在执行，状态为 Running
        var state = isExecuting 
            ? JobState.Running 
            : DetermineJobState(primaryTrigger, triggerState);

        var tz = ResolveJobTimezone(jobDetail);
        return new JobStatus
        {
            JobId = jobKey.Name,
            JobName = jobDetail.Description ?? jobKey.Name,
            JobGroup = jobKey.Group,
            State = state,
            PreviousFireTime = ToScheduleLocal(GetLatestPreviousFireUtc(triggers, jobKey.Name), tz),
            NextFireTime = ToScheduleLocal(GetEarliestNextFireUtc(triggers, jobKey.Name), tz),
            TriggerType = primaryTrigger?.GetType().Name,
            CronExpression = primaryTrigger is ICronTrigger cronTrigger ? cronTrigger.CronExpressionString : null
        };
    }

    private static JobState DetermineJobState(ITrigger? trigger, TriggerState triggerState)
    {
        return triggerState switch
        {
            TriggerState.Normal => JobState.Scheduled,
            TriggerState.Paused => JobState.Paused,
            TriggerState.Complete => JobState.Disabled,
            _ => JobState.Scheduled
        };
    }
    
    /// <summary>获取主 trigger（忽略临时 trigger）</summary>
    private static ITrigger? GetPrimaryTrigger(IReadOnlyCollection<ITrigger> triggers, string jobId)
    {
        // 优先匹配标准命名的主 trigger
        var primaryTrigger = triggers.FirstOrDefault(t => t.Key.Name == $"{jobId}_trigger");
        if (primaryTrigger != null) return primaryTrigger;

        // 窗口 trigger：{jobId}__w*
        var windowTrigger = triggers
            .Where(t => t.Key.Name.StartsWith($"{jobId}__w", StringComparison.Ordinal))
            .OrderBy(t => t.GetNextFireTimeUtc() ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();
        if (windowTrigger != null) return windowTrigger;
        
        // 否则返回第一个非临时 trigger
        return triggers.FirstOrDefault(t => 
            !t.Key.Name.StartsWith("MT_") && 
            !t.Key.Name.StartsWith("fire_"));
    }

    private static IEnumerable<ITrigger> GetPersistentTriggers(IReadOnlyCollection<ITrigger> triggers, string jobId)
    {
        return triggers.Where(t =>
            !t.Key.Name.StartsWith("MT_", StringComparison.Ordinal) &&
            !t.Key.Name.StartsWith("fire_", StringComparison.Ordinal));
    }

    private static DateTimeOffset? GetEarliestNextFireUtc(IReadOnlyCollection<ITrigger> triggers, string jobId)
    {
        DateTimeOffset? best = null;
        foreach (var fire in GetPersistentTriggers(triggers, jobId).Select(t => t.GetNextFireTimeUtc()))
        {
            if (!fire.HasValue) continue;
            if (best == null || fire.Value < best.Value)
                best = fire;
        }
        return best;
    }

    private static DateTimeOffset? GetLatestPreviousFireUtc(IReadOnlyCollection<ITrigger> triggers, string jobId)
    {
        var values = GetPersistentTriggers(triggers, jobId)
            .Select(t => t.GetPreviousFireTimeUtc())
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    private static TimeZoneInfo ResolveJobTimezone(IJobDetail jobDetail)
    {
        var tzId = jobDetail.JobDataMap.ContainsKey(Jobs.JobDataKeys.ScheduleTimezone)
            ? jobDetail.JobDataMap.GetString(Jobs.JobDataKeys.ScheduleTimezone)
            : null;
        return ScheduleExpressionParser.ResolveTimeZone(tzId);
    }

    private static DateTime? ToScheduleLocal(DateTimeOffset? utc, TimeZoneInfo tz)
    {
        if (utc == null) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc.Value.UtcDateTime, tz);
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
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
        ScheduleIntent intent,
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

            // 如果不是 Active 状态，不创建任务
            if (intent != ScheduleIntent.Active)
            {
                _logger.LogDebug("Job {JobId} intent={Intent}, skipping creation", jobId, intent);
                return;
            }

            // 创建 JobDetail
            var jobType = jobId == SchedulerConstants.HeartbeatJobId 
                ? typeof(Jobs.HeartbeatJob) 
                : typeof(Jobs.AgentJob);

            var mergedData = jobData ?? new JobDataMap();
            mergedData[Jobs.JobDataKeys.ScheduleTimezone] = schedule.Timezone ?? TimeZoneInfo.Local.Id;

            var jobBuilder = JobBuilder.Create(jobType)
                .WithIdentity(jobKey)
                .WithDescription(jobId)
                .StoreDurably()
                .UsingJobData(mergedData);

            var jobDetail = jobBuilder.Build();

            // 创建触发器（可能多个 windows）
            var triggers = BuildTriggers(jobId, schedule);
            await _scheduler.ScheduleJob(jobDetail, new HashSet<ITrigger>(triggers), replace: true, ct);
            _logger.LogDebug("Job {JobId} scheduled with {Count} trigger(s)", jobId, triggers.Count);
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

    /// <summary>立即触发任务（使用原生 TriggerJob，传递 RunId）</summary>
    public async Task TriggerJobAsync(string jobId, string runId, JobDataMap? additionalData = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            
            // 使用原生 TriggerJob，不创建新的 trigger
            var jobDataMap = new JobDataMap { [JobDataKeys.RunId] = runId };
            if (additionalData != null)
            {
                foreach (var key in additionalData.Keys)
                {
                    jobDataMap[key] = additionalData[key];
                }
            }
            
            await _scheduler.TriggerJob(jobKey, jobDataMap, ct);
            _logger.LogDebug("Job {JobId} triggered directly with RunId={RunId}", jobId, runId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>取消正在执行的任务</summary>
    /// <returns>如果任务正在运行且成功中断返回 true，否则返回 false</returns>
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_scheduler == null) return false;

            var jobKey = new JobKey(jobId, SchedulerConstants.DefaultJobGroup);
            
            // 使用 Quartz 的 Interrupt 方法，会触发 CancellationToken
            var result = await _scheduler.Interrupt(jobKey, ct);
            if (result)
            {
                _logger.LogInformation("Job {JobId} cancellation requested successfully", jobId);
            }
            else
            {
                _logger.LogDebug("Job {JobId} was not running or could not be interrupted", jobId);
            }
            
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>构建触发器列表</summary>
    private IReadOnlyList<ITrigger> BuildTriggers(string jobId, ScheduleSpec schedule)
    {
        if (!ScheduleWindowsValidator.ValidateForScheduleType(schedule.Type, schedule.Windows, out var windowsError))
            throw new InvalidOperationException(windowsError ?? "无效的生效时段");

        var timezone = ScheduleExpressionParser.ResolveTimeZone(schedule.Timezone);

        if (schedule.Type == ScheduleTypes.Interval &&
            schedule.Windows is { Count: > 0 })
        {
            return BuildWindowIntervalTriggers(jobId, schedule, timezone);
        }

        return new[] { BuildSingleTrigger(jobId, schedule, timezone) };
    }

    private IReadOnlyList<ITrigger> BuildWindowIntervalTriggers(string jobId, ScheduleSpec schedule, TimeZoneInfo timezone)
    {
        if (string.IsNullOrEmpty(schedule.Every))
            throw new InvalidOperationException("Interval schedule requires 'every'");

        if (!ScheduleWindowsValidator.TryNormalize(schedule.Windows, out var windows, out var error))
            throw new InvalidOperationException(error ?? "无效的生效时段");

        var interval = ScheduleExpressionParser.ParseInterval(schedule.Every);
        var triggers = new List<ITrigger>();

        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (!w.CrossesMidnight)
            {
                triggers.Add(BuildDailyIntervalTrigger(
                    $"{jobId}__w{i}", jobId, w.Start, w.End, interval, timezone));
            }
            else
            {
                triggers.Add(BuildDailyIntervalTrigger(
                    $"{jobId}__w{i}_a", jobId, w.Start, ScheduleWindowsValidator.DefaultEnd, interval, timezone));
                triggers.Add(BuildDailyIntervalTrigger(
                    $"{jobId}__w{i}_b", jobId, TimeSpan.Zero, w.End, interval, timezone));
            }
        }

        return triggers;
    }

    private static ITrigger BuildDailyIntervalTrigger(
        string triggerName,
        string jobId,
        TimeSpan start,
        TimeSpan end,
        TimeSpan every,
        TimeZoneInfo timezone)
    {
        var startTod = TimeOfDay.HourMinuteAndSecondOfDay(start.Hours, start.Minutes, start.Seconds);
        var endTod = TimeOfDay.HourMinuteAndSecondOfDay(end.Hours, end.Minutes, end.Seconds);
        var seconds = Math.Max(1, (int)Math.Round(every.TotalSeconds));

        return TriggerBuilder.Create()
            .WithIdentity(triggerName, SchedulerConstants.DefaultTriggerGroup)
            .ForJob(jobId, SchedulerConstants.DefaultJobGroup)
            .WithDailyTimeIntervalSchedule(x =>
            {
                x.StartingDailyAt(startTod);
                x.EndingDailyAt(endTod);
                x.WithInterval(seconds, IntervalUnit.Second);
                x.OnEveryDay();
                x.InTimeZone(timezone);
                x.WithMisfireHandlingInstructionDoNothing();
            })
            .Build();
    }

    private ITrigger BuildSingleTrigger(string jobId, ScheduleSpec schedule, TimeZoneInfo timezone)
    {
        var triggerKey = new TriggerKey(jobId + "_trigger", SchedulerConstants.DefaultTriggerGroup);

        // Cron 和 Once 从当前时间开始，Interval 从下一个整周期开始避免立即触发
        var startNow = schedule.Type != ScheduleTypes.Interval;
        var triggerBuilder = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobId, SchedulerConstants.DefaultJobGroup);

        if (startNow)
            triggerBuilder.StartNow();
        else
            triggerBuilder.StartAt(DateBuilder.FutureDate(5, IntervalUnit.Second));

        switch (schedule.Type)
        {
            case ScheduleTypes.Cron:
                if (string.IsNullOrEmpty(schedule.Cron))
                    throw new InvalidOperationException("Cron schedule requires 'cron' expression");

                var cron = ScheduleExpressionParser.NormalizeCron(schedule.Cron);
                triggerBuilder.WithCronSchedule(cron, x =>
                {
                    x.InTimeZone(timezone);
                    x.WithMisfireHandlingInstructionDoNothing();
                });
                break;

            case ScheduleTypes.Interval:
                if (string.IsNullOrEmpty(schedule.Every))
                    throw new InvalidOperationException("Interval schedule requires 'every'");

                var interval = ScheduleExpressionParser.ParseInterval(schedule.Every);
                triggerBuilder.WithSimpleSchedule(x => x
                    .WithInterval(interval)
                    .RepeatForever()
                    .WithMisfireHandlingInstructionNextWithExistingCount());
                break;

            case ScheduleTypes.Once:
                if (schedule.RunAt == null)
                    throw new InvalidOperationException("Once schedule requires 'runAt'");

                // RunAt 是本地墙钟：按 schedule 时区解释后转 UTC
                var runAtUnspec = DateTime.SpecifyKind(schedule.RunAt.Value, DateTimeKind.Unspecified);
                var runAtUtc = TimeZoneInfo.ConvertTimeToUtc(runAtUnspec, timezone);
                triggerBuilder.StartAt(runAtUtc);
                triggerBuilder.WithSimpleSchedule(x => x
                    .WithRepeatCount(0)
                    .WithMisfireHandlingInstructionNextWithExistingCount());
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