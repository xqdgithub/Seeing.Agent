using Microsoft.Extensions.Logging;
using Quartz;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Management;

/// <summary>调度管理器 — Quartz 包装，提供 CRUD、状态查询、生命周期管理</summary>
public sealed class ScheduleManager : IScheduleManager, IJobExecutionListener
{
    private readonly IScheduleRepository _repository;
    private readonly QuartzSchedulerEngine _engine;
    private readonly ISchedulerOptionsProvider _optionsProvider;
    private readonly ISessionManager? _sessions;
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
        ILogger<ScheduleManager> logger,
        ISessionManager? sessions = null)
    {
        _repository = repository;
        _engine = engine;
        _optionsProvider = optionsProvider;
        _logger = logger;
        _sessions = sessions;
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

            // 升级/重启兜底：先清掉持久化存储里的旧心跳触发器，避免它按旧计划 misfire 一次，
            // 随后又按新锚点触发一次。用户任务的持久化触发器不动。
            await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);

            // 启动 Quartz 调度器
            await _engine.StartAsync(ct);

            // 已有任务若只绑了会话、还没有 conversation，从会话出站绑定补一份快照。
            // 否则会话归档后 ChannelRoute 不可用，到点无法推送。
            if (await SnapshotMissingConversationsAsync(ct).ConfigureAwait(false))
                await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);

            // 注册用户任务（只注册 Active 状态的）；单个任务失败不阻断其余任务
            foreach (var job in _jobsFile.Jobs.Where(j => j.Intent == ScheduleIntent.Active))
            {
                try
                {
                    await RegisterUserJobAsync(job, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "注册定时任务失败，已跳过: JobId={JobId}", job.Id);
                }
            }

            // 注册心跳（内部任务，失败同样不阻断用户任务）
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
        var lockTaken = false;
        try
        {
            await _lifecycleLock.WaitAsync(ct);
            lockTaken = true;
            if (!_started)
                return;

            await _engine.StopAsync(ct);
            _runningJobs.Clear();
            _started = false;
            _logger.LogInformation("ScheduleManager stopped");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host 停止超时后会取消共享令牌。此时进程已经进入终止清理阶段，
            // 不应把正常的停止期限取消升级为 Generic Host 的 AggregateException。
            _logger.LogWarning("ScheduleManager stop canceled by host shutdown deadline");
        }
        finally
        {
            if (lockTaken)
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
        // 用户任务集合优先，其次内部槽位（心跳）。心跳不在 Jobs 里，但状态必须可查。
        var job = FindJob(jobId);
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
        // 保留 id 只属于内部任务：放进 Jobs 会让它出现在列表/工具/统计里，并与内部槽位抢 Quartz key。
        if (ScheduleIdPolicy.IsReserved(job.Id))
            throw new InvalidOperationException(
                $"'{job.Id}' 是平台保留任务 id，不能通过用户任务 API 创建或替换。");

        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N");

        await SnapshotConversationAsync(job, ct).ConfigureAwait(false);

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
        if (ScheduleIdPolicy.IsReserved(jobId))
            throw new InvalidOperationException(
                $"'{jobId}' 是平台保留任务 id，不能通过用户任务 API 删除。");

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
        // 内部任务（心跳）不提供按 id 触发；立即执行走 IHeartbeatService.RunNowAsync。
        // 这里必须返回 NotFound：cron_run 在 GetJobAsync 为空时也会调本方法，
        // 若在这里放行，等于给了一条绕过心跳服务、且不带执行画像的触发路径。
        if (ScheduleIdPolicy.IsReserved(jobId))
            return new TriggerResult.NotFound();

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
        if (ScheduleIdPolicy.IsReserved(jobId))
            throw new InvalidOperationException(
                $"'{jobId}' 是平台保留任务 id，不能通过用户任务 API 改意图。");

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
            DispatchSessionId = result.DispatchSessionId
        };

        // 心跳历史同样落盘：_heartbeat.json 是合法文件名。内部任务缺执行记录时，
        // 用户与页面都无从判断「上次跑成没跑成」。
        await _repository.AppendHistoryAsync(jobId, record, ct);

        // 更新任务最后运行时间：槽位任务也要回写，否则 interval 锚点永远为空。
        var job = FindJob(jobId);
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

    /// <summary>
    /// 任务定义查找：用户任务集合优先，其次心跳槽位。
    /// <para>仅供状态查询与执行结果回写使用；列表类出口只遍历 <c>Jobs</c>，不会看到心跳。</para>
    /// </summary>
    private ScheduledJobSpec? FindJob(string jobId)
        => _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId)
           ?? (_jobsFile.Heartbeat is { } hb && string.Equals(hb.Id, jobId, StringComparison.Ordinal)
               ? hb
               : null);

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

    private async Task<bool> SnapshotMissingConversationsAsync(CancellationToken ct)
    {
        var changed = false;
        foreach (var job in _jobsFile.Jobs)
        {
            if (await SnapshotConversationAsync(job, ct).ConfigureAwait(false))
                changed = true;
        }

        return changed;
    }

    /// <summary>任务 conversation 不完整时，从目标会话的出站绑定抄一份快照。</summary>
    private async Task<bool> SnapshotConversationAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_sessions is null)
            return false;

        var sessionId = job.Dispatch.Target.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || job.Dispatch.Target.Conversation is { IsComplete: true })
            return false;

        var session = await _sessions.TryGetOrLoadAsync(sessionId).ConfigureAwait(false);
        return ConversationSpec.TryCopyFromRoute(job.Dispatch.Target, session?.ChannelRoute);
    }

    private async Task RegisterUserJobAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        var jobData = new JobDataMap
        {
            [JobDataKeys.JobId] = job.Id,
            [JobDataKeys.JobName] = job.Name ?? job.Id,
            [JobDataKeys.TaskType] = job.TaskType,
            // 仅显式配置才写 SessionId 键：未配置时省略，任务侧据此区分
            // 「显式会话（挂绑 conversation）」与「仅 conversation（每次触发新建会话）」
            [JobDataKeys.TimeoutSeconds] = job.Runtime.TimeoutSeconds.ToString()
        };

        if (!string.IsNullOrEmpty(job.Dispatch.Target.SessionId))
            jobData[JobDataKeys.SessionId] = job.Dispatch.Target.SessionId;

        if (job.TaskType == ScheduleTaskTypes.Text)
        {
            jobData[JobDataKeys.Text] = job.Text ?? string.Empty;
        }
        else
        {
            jobData[JobDataKeys.AgentId] = job.Agent ?? string.Empty;
            jobData[JobDataKeys.Prompt] = job.Prompt ?? string.Empty;
            if (!string.IsNullOrEmpty(job.Model))
                jobData[JobDataKeys.Model] = job.Model;
            if (!string.IsNullOrEmpty(job.Mode))
                jobData[JobDataKeys.Mode] = job.Mode;
        }

        // 投递配置
        if (!string.IsNullOrEmpty(job.Dispatch.Target.SessionId))
            jobData[JobDataKeys.DispatchSessionId] = job.Dispatch.Target.SessionId;

        // 投递 conversation 目标（四键；任务侧按齐备性判定生效）
        var conversation = job.Dispatch.Target.Conversation;
        if (conversation is { IsComplete: true })
        {
            jobData[JobDataKeys.ConversationChannelId] = conversation.ChannelId!.Trim();
            jobData[JobDataKeys.ConversationKind] = conversation.Kind!.Trim().ToLowerInvariant();
            jobData[JobDataKeys.ConversationExternalId] = conversation.ExternalId!.Trim();
            if (!string.IsNullOrWhiteSpace(conversation.Title))
                jobData[JobDataKeys.ConversationTitle] = conversation.Title!.Trim();
        }

        await _engine.UpsertJobAsync(job.Id, job.Schedule, job.Intent, jobData, ct);
    }

    // ===== 心跳槽位（供 IHeartbeatService 转调；不进入 IScheduleManager）=====

    /// <summary>心跳槽位快照（深拷贝）。null 表示尚未配置。</summary>
    internal ScheduledJobSpec? HeartbeatSpec()
        => _jobsFile.Heartbeat is null ? null : CloneSpec(_jobsFile.Heartbeat);

    /// <summary>保存心跳槽位：写盘，并按 intent 注册或移除触发器。</summary>
    internal async Task<ScheduledJobSpec> SaveHeartbeatAsync(ScheduledJobSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.Equals(spec.Id, SchedulerConstants.HeartbeatJobId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"心跳任务 id 必须为 '{SchedulerConstants.HeartbeatJobId}'，实际为 '{spec.Id}'。");

        var stored = CloneSpec(spec);
        stored.Id = SchedulerConstants.HeartbeatJobId;
        _jobsFile.Heartbeat = stored;
        await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);

        if (_started)
            await RegisterHeartbeatAsync(ct).ConfigureAwait(false);

        return CloneSpec(stored);
    }

    /// <summary>在槽位副本上就地修改并保存（供心跳服务做局部更新）。槽位为 null 时返回 null。</summary>
    internal async Task<ScheduledJobSpec?> MutateHeartbeatAsync(
        Action<ScheduledJobSpec> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        if (_jobsFile.Heartbeat is null)
            return null;

        var copy = CloneSpec(_jobsFile.Heartbeat);
        mutate(copy);
        return await SaveHeartbeatAsync(copy, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 立即执行心跳。
    /// <para>
    /// 四种结果都有明确语义：未配置 → <c>NotFound</c>；调度器未启动或心跳未启用 → <c>Disabled</c>；
    /// 正在执行 → <c>Conflict</c>；其余 <c>Accepted</c>。绝不出现「说已触发但其实没跑」。
    /// </para>
    /// </summary>
    internal async Task<TriggerResult> RunHeartbeatAsync(CancellationToken ct = default)
    {
        var hb = _jobsFile.Heartbeat;
        if (hb is null)
            return new TriggerResult.NotFound();

        if (!_started || _engine.Scheduler is null)
            return new TriggerResult.Disabled();

        if (hb.Intent != ScheduleIntent.Active)
            return new TriggerResult.Disabled();

        var runId = Guid.NewGuid().ToString("N");
        if (!_runningJobs.TryAdd(SchedulerConstants.HeartbeatJobId, new RunInfo(runId, DateTime.Now)))
            return new TriggerResult.Conflict("任务正在执行中");

        try
        {
            // 先按当前槽位与锚点重建触发器，确保立即执行与周期执行走同一份配置。
            await RegisterHeartbeatAsync(ct).ConfigureAwait(false);
            await _engine.TriggerJobAsync(
                SchedulerConstants.HeartbeatJobId,
                runId,
                new JobDataMap { [JobDataKeys.Source] = ScheduleSources.Manual },
                ct).ConfigureAwait(false);

            _logger.LogInformation("Heartbeat triggered with RunId={RunId}", runId);
            return new TriggerResult.Accepted(runId);
        }
        catch
        {
            _runningJobs.TryRemove(SchedulerConstants.HeartbeatJobId, out _);
            throw;
        }
    }

    /// <summary>取消正在执行的心跳。</summary>
    internal async Task<bool> CancelHeartbeatAsync(CancellationToken ct = default)
    {
        if (_jobsFile.Heartbeat is null)
            return false;

        return await CancelJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
    }

    private async Task RegisterHeartbeatAsync(CancellationToken ct)
    {
        var hb = _jobsFile.Heartbeat;
        if (hb is null)
        {
            // 槽位被清空：移除残留触发器，避免「已没有配置却还在按时跑」。
            await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _engine.UpsertJobAsync(
                SchedulerConstants.HeartbeatJobId,
                hb.Schedule,
                hb.Intent,
                BuildHeartbeatData(hb),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单条配置写错不应该让整个调度器起不来（用户任务照常注册）。
            _logger.LogError(ex, "心跳注册失败（不影响其他任务）: Every={Every}", hb.Schedule.Every);
        }
    }

    private JobDataMap BuildHeartbeatData(ScheduledJobSpec hb)
    {
        var data = new JobDataMap
        {
            [JobDataKeys.JobId] = SchedulerConstants.HeartbeatJobId,
            [JobDataKeys.JobName] = hb.Name ?? "心跳",
            [JobDataKeys.TaskType] = ScheduleTaskTypes.Agent,
            [JobDataKeys.Prompt] = hb.Prompt ?? string.Empty,
            [JobDataKeys.AgentId] = hb.Agent ?? string.Empty,
            [JobDataKeys.TimeoutSeconds] = hb.Runtime.TimeoutSeconds.ToString(),
            [JobDataKeys.Source] = ScheduleSources.Heartbeat
        };

        if (!string.IsNullOrWhiteSpace(hb.Dispatch?.Target?.SessionId))
            data[JobDataKeys.SessionId] = hb.Dispatch.Target.SessionId;

        if (!string.IsNullOrWhiteSpace(hb.Model))
            data[JobDataKeys.Model] = hb.Model;

        if (!string.IsNullOrWhiteSpace(hb.Mode))
            data[JobDataKeys.Mode] = hb.Mode;

        var conversation = hb.Dispatch?.Target?.Conversation;
        if (conversation is { IsComplete: true })
        {
            data[JobDataKeys.ConversationChannelId] = conversation.ChannelId!.Trim();
            data[JobDataKeys.ConversationKind] = conversation.Kind!.Trim().ToLowerInvariant();
            data[JobDataKeys.ConversationExternalId] = conversation.ExternalId!.Trim();
            if (!string.IsNullOrWhiteSpace(conversation.Title))
                data[JobDataKeys.ConversationTitle] = conversation.Title!.Trim();
        }

        data.SetJsonValue(JobDataKeys.RunProfile, ScheduleRunProfile.Heartbeat);

        // interval 锚点：心跳必须始终带锚点，否则会落进「用户任务启动后 5 秒首触发」那条分支。
        // 首次配置没有上次执行时间时用当前时刻，语义即「从现在起算一个完整周期」。
        var anchor = hb.LastRunAt ?? DateTime.Now;
        data[JobDataKeys.LastRunAt] = anchor.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        return data;
    }

    private static ScheduledJobSpec CloneSpec(ScheduledJobSpec spec)
        => System.Text.Json.JsonSerializer.Deserialize<ScheduledJobSpec>(
               System.Text.Json.JsonSerializer.Serialize(spec, SeeingJson.Readable),
               SeeingJson.Readable)
           ?? new ScheduledJobSpec();
}
