using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Management;

/// <summary>调度管理器 — CRUD、引擎生命周期、心跳注册</summary>
public sealed class ScheduleManager : IScheduleManager
{
    private readonly IScheduleRepository _repository;
    private readonly IScheduledJobExecutor _executor;
    private readonly IHeartbeatRunner _heartbeatRunner;
    private readonly InProcessSchedulerEngine _engine;
    private readonly SchedulerOptionsProvider _optionsProvider;
    private readonly ILogger<ScheduleManager> _logger;
    private readonly SemaphoreSlim _runLock;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private JobsFile _jobsFile = new();
    private bool _started;

    public ScheduleManager(
        IScheduleRepository repository,
        IScheduledJobExecutor executor,
        IHeartbeatRunner heartbeatRunner,
        InProcessSchedulerEngine engine,
        SchedulerOptionsProvider optionsProvider,
        ILogger<ScheduleManager> logger)
    {
        _repository = repository;
        _executor = executor;
        _heartbeatRunner = heartbeatRunner;
        _engine = engine;
        _optionsProvider = optionsProvider;
        _logger = logger;

        var maxConcurrent = Math.Max(1, optionsProvider.Current.MaxConcurrentJobs);
        _runLock = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <inheritdoc/>
    public bool IsStarted => _started;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
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

            _jobsFile = await _repository.LoadAsync(ct).ConfigureAwait(false);
            _engine.Configure(
                TimeSpan.FromSeconds(Math.Max(1, options.TickIntervalSeconds)),
                options.Timezone);

            await _engine.StartAsync(OnJobDueAsync, ct).ConfigureAwait(false);

            foreach (var job in _jobsFile.Jobs.Where(j => j.Enabled))
                await RegisterUserJobAsync(job, ct).ConfigureAwait(false);

            await RegisterHeartbeatAsync(ct).ConfigureAwait(false);

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
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_started)
                return;

            await _engine.StopAsync().ConfigureAwait(false);
            _started = false;
            _logger.LogInformation("ScheduleManager stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
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

        await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);

        if (_started)
            await RegisterUserJobAsync(job, ct).ConfigureAwait(false);

        return job;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default)
    {
        var removed = _jobsFile.Jobs.RemoveAll(j => j.Id == jobId) > 0;
        if (!removed)
            return false;

        await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);
        await _engine.RemoveJobAsync(jobId, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<JobExecutionResult> RunJobOnceAsync(string jobId, CancellationToken ct = default)
    {
        if (jobId == SchedulerConstants.HeartbeatJobId)
            return await _heartbeatRunner.RunOnceAsync(ct).ConfigureAwait(false);

        var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' not found");

        return await ExecuteJobInternalAsync(job, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReloadHeartbeatAsync(CancellationToken ct = default)
    {
        _optionsProvider.Reload();
        if (!_started)
            return;

        await _engine.RemoveJobAsync(SchedulerConstants.HeartbeatJobId, ct).ConfigureAwait(false);
        await RegisterHeartbeatAsync(ct).ConfigureAwait(false);
    }

    private async Task RegisterUserJobAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        var schedule = job.Schedule;
        schedule.Every ??= schedule.Type == ScheduleTypes.Interval ? "1h" : null;

        var next = ScheduleExpressionParser.GetNextOccurrence(schedule, DateTimeOffset.UtcNow, _optionsProvider.Current.Timezone);
        job.NextRunAt = next;

        await _engine.UpsertJobAsync(job.Id, schedule, job.Enabled, ct).ConfigureAwait(false);
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

        await _engine.UpsertJobAsync(SchedulerConstants.HeartbeatJobId, schedule, true, ct).ConfigureAwait(false);
    }

    private async Task OnJobDueAsync(string jobId, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Skipping job {JobId}: max concurrent jobs reached", jobId);
            return;
        }

        try
        {
            if (jobId == SchedulerConstants.HeartbeatJobId)
            {
                await _heartbeatRunner.RunOnceAsync(ct).ConfigureAwait(false);
            }
            else
            {
                var job = _jobsFile.Jobs.FirstOrDefault(j => j.Id == jobId);
                if (job == null || !job.Enabled)
                    return;

                await ExecuteJobInternalAsync(job, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await _engine.RescheduleAfterRunAsync(jobId, ct).ConfigureAwait(false);
            _runLock.Release();
        }
    }

    private async Task<JobExecutionResult> ExecuteJobInternalAsync(ScheduledJobSpec job, CancellationToken ct)
    {
        var record = new JobExecutionRecord
        {
            Source = ScheduleSources.Cron,
            StartedAt = DateTimeOffset.UtcNow
        };

        JobExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(job, ct).ConfigureAwait(false);
            record.Status = result.Success ? "success" : "failed";
            record.Output = result.Output;
            record.Error = result.Error;
        }
        catch (Exception ex)
        {
            result = new JobExecutionResult
            {
                Success = false,
                Error = ex.Message,
                Source = ScheduleSources.Cron
            };
            record.Status = "failed";
            record.Error = ex.Message;
        }

        record.CompletedAt = DateTimeOffset.UtcNow;
        job.LastRunAt = record.StartedAt;

        if (!job.Id.StartsWith('_'))
            await _repository.AppendHistoryAsync(job.Id, record, ct).ConfigureAwait(false);

        await _repository.SaveAsync(_jobsFile, ct).ConfigureAwait(false);
        return result;
    }
}
