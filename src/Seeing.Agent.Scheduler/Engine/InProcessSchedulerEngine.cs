using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Engine;

/// <summary>进程内轻量调度引擎</summary>
public sealed class InProcessSchedulerEngine : IAsyncDisposable
{
    private readonly ILogger<InProcessSchedulerEngine> _logger;
    private readonly Dictionary<string, ScheduledEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private TimeSpan _tickInterval = SchedulerConstants.DefaultTickInterval;
    private string _timezone = "UTC";

    public InProcessSchedulerEngine(ILogger<InProcessSchedulerEngine> logger)
    {
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public void Configure(TimeSpan tickInterval, string timezone)
    {
        _tickInterval = tickInterval > TimeSpan.Zero ? tickInterval : SchedulerConstants.DefaultTickInterval;
        _timezone = timezone;
    }

    public async Task StartAsync(Func<string, CancellationToken, Task> onDue, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _timer = new PeriodicTimer(_tickInterval);
            IsRunning = true;
            _ = RunLoopAsync(onDue, _cts.Token);
            _logger.LogDebug("Scheduler engine started (tick={Tick})", _tickInterval);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning)
                return;

            _cts?.Cancel();
            _timer?.Dispose();
            _timer = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _logger.LogDebug("Scheduler engine stopped");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertJobAsync(string jobId, ScheduleSpec schedule, bool enabled, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                _entries.Remove(jobId);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var next = ScheduleExpressionParser.GetNextOccurrence(schedule, now, _timezone)
                       ?? now;

            _entries[jobId] = new ScheduledEntry
            {
                JobId = jobId,
                Schedule = schedule,
                NextRunAt = next,
                IsInternal = jobId.StartsWith('_')
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveJobAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.Remove(jobId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RescheduleAfterRunAsync(string jobId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(jobId, out var entry))
                return;

            var now = DateTimeOffset.UtcNow;
            entry.LastRunAt = now;

            if (entry.Schedule.Type == ScheduleTypes.Once)
            {
                _entries.Remove(jobId);
                return;
            }

            entry.NextRunAt = ScheduleExpressionParser.GetNextOccurrence(entry.Schedule, now, _timezone) ?? now;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lock.Dispose();
    }

    private async Task RunLoopAsync(Func<string, CancellationToken, Task> onDue, CancellationToken ct)
    {
        if (_timer == null)
            return;

        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                List<string> dueJobs;
                await _lock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    dueJobs = _entries.Values
                        .Where(e => e.NextRunAt <= now)
                        .Select(e => e.JobId)
                        .ToList();
                }
                finally
                {
                    _lock.Release();
                }

                foreach (var jobId in dueJobs)
                {
                    try
                    {
                        await onDue(jobId, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduler callback failed for job {JobId}", jobId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private sealed class ScheduledEntry
    {
        public required string JobId { get; init; }
        public required ScheduleSpec Schedule { get; init; }
        public DateTimeOffset NextRunAt { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public bool IsInternal { get; init; }
    }
}
