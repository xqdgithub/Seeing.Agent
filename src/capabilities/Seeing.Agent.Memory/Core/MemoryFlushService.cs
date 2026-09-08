using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core;

public sealed class MemoryFlushService : IMemoryFlushService
{
    private readonly ISessionMemoryBuffer _buffer;
    private readonly IMemoryWorkQueue _queue;
    private readonly IMemoryPipeline _pipeline;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<MemoryFlushService>? _logger;
    private readonly ConcurrentQueue<DateTimeOffset> _flushTimes = new();
    private readonly object _rateLock = new();

    public MemoryFlushService(
        ISessionMemoryBuffer buffer,
        IMemoryWorkQueue queue,
        IMemoryPipeline pipeline,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<MemoryFlushService>? logger = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public bool TryEnqueueBatch(MemoryBatch batch)
    {
        if (batch.Candidates.Count == 0)
            return false;

        if (!TryAcquireFlushSlot())
        {
            _logger?.LogWarning(
                "Memory flush rate limited, re-buffering {Count} candidates Session={SessionId}",
                batch.Candidates.Count,
                batch.SessionId);
            Rebuffer(batch);
            return false;
        }

        if (!_queue.TryEnqueue(batch))
        {
            _logger?.LogWarning(
                "Memory queue full, re-buffering {Count} candidates Session={SessionId}",
                batch.Candidates.Count,
                batch.SessionId);
            Rebuffer(batch);
            return false;
        }

        _logger?.LogInformation(
            "Memory batch enqueued Session={SessionId} Candidates={Count}",
            batch.SessionId,
            batch.Candidates.Count);
        return true;
    }

    public bool TryFlushSession(string sessionId)
    {
        var batch = _buffer.TakeAll(sessionId);
        return batch is not null && TryEnqueueBatch(batch);
    }

    public async Task FlushSessionInlineAsync(string sessionId, CancellationToken ct = default)
    {
        var batch = _buffer.TakeAll(sessionId);
        if (batch is null)
            return;

        if (!TryAcquireFlushSlot())
        {
            _logger?.LogWarning(
                "Memory inline flush rate limited, re-buffering Session={SessionId}",
                sessionId);
            Rebuffer(batch);
            return;
        }

        try
        {
            var result = await _pipeline.ProcessBatchAsync(batch, ct);
            _logger?.LogInformation(
                "Memory inline flush Session={SessionId} Stored={Count} Reason={Reason}",
                sessionId,
                result.StoredCount,
                result.Reason);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Memory inline flush failed Session={SessionId}", sessionId);
            Rebuffer(batch);
        }
    }

    public bool TryFlushAfterTurn(string sessionId)
    {
        var batch = _buffer.OnAgentTurnCompleted(sessionId);
        if (batch is null)
            return false;

        if (TryEnqueueBatch(batch))
            return true;

        var everyN = _options.CurrentValue.Extraction.ExtractEveryNTurns;
        if (everyN > 0)
            _buffer.SetTurnCount(sessionId, everyN);

        return false;
    }

    public int FlushIdleSessions()
    {
        var minutes = _options.CurrentValue.Extraction.FlushIdleMinutes;
        if (minutes <= 0)
            return 0;

        var batches = _buffer.TakeIdleBatches(TimeSpan.FromMinutes(minutes));
        var flushed = 0;
        foreach (var batch in batches)
        {
            if (TryEnqueueBatch(batch))
                flushed++;
        }

        return flushed;
    }

    private void Rebuffer(MemoryBatch batch)
    {
        _buffer.Requeue(batch.SessionId, batch.Candidates);
    }

    private bool TryAcquireFlushSlot()
    {
        var max = Math.Max(1, _options.CurrentValue.Extraction.MaxCandidatesPerMinute);
        var now = DateTimeOffset.UtcNow;
        lock (_rateLock)
        {
            while (_flushTimes.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(1))
                _flushTimes.TryDequeue(out _);

            if (_flushTimes.Count >= max)
                return false;

            _flushTimes.Enqueue(now);
            return true;
        }
    }
}
