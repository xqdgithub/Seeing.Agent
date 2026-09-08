using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core;

public sealed class SessionMemoryBuffer : ISessionMemoryBuffer
{
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    public SessionMemoryBuffer(IOptionsMonitor<MemoryOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public MemoryBatch? Add(MemoryCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.SessionId))
            return null;

        var opts = _options.CurrentValue.Extraction;
        var maxSnippets = Math.Max(1, opts.MaxBufferedSnippets);
        var maxChars = Math.Max(1, opts.MaxBatchChars);

        MemoryBatch? overflow = null;
        var state = _sessions.GetOrAdd(candidate.SessionId, _ => new SessionState());
        lock (state.Gate)
        {
            state.LastActivityUtc = DateTimeOffset.UtcNow;

            var nextChars = state.TotalChars + candidate.Snippet.Length;
            if (state.Items.Count >= maxSnippets || nextChars > maxChars)
            {
                overflow = TakeLocked(candidate.SessionId, state);
                state.LastActivityUtc = DateTimeOffset.UtcNow;
            }

            state.Items.Add(candidate);
            state.TotalChars += candidate.Snippet.Length;
        }

        return overflow;
    }

    public MemoryBatch? OnAgentTurnCompleted(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var everyN = _options.CurrentValue.Extraction.ExtractEveryNTurns;
        if (everyN <= 0)
            return null;

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        lock (state.Gate)
        {
            state.TurnCount++;
            state.LastActivityUtc = DateTimeOffset.UtcNow;
            if (state.TurnCount < everyN || state.Items.Count == 0)
                return null;

            var batch = TakeLocked(sessionId, state);
            state.TurnCount = 0;
            return batch;
        }
    }

    public MemoryBatch? TakeAll(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;

        lock (state.Gate)
        {
            var batch = TakeLocked(sessionId, state);
            state.TurnCount = 0;
            return batch;
        }
    }

    public IReadOnlyList<MemoryBatch> TakeIdleBatches(TimeSpan idle)
    {
        if (idle <= TimeSpan.Zero)
            return Array.Empty<MemoryBatch>();

        var cutoff = DateTimeOffset.UtcNow - idle;
        var result = new List<MemoryBatch>();

        foreach (var kv in _sessions)
        {
            var state = kv.Value;
            lock (state.Gate)
            {
                if (state.Items.Count == 0 || state.LastActivityUtc > cutoff)
                    continue;

                var batch = TakeLocked(kv.Key, state);
                if (batch is not null)
                    result.Add(batch);
            }
        }

        return result;
    }

    public int GetPendingCount(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return 0;
        lock (state.Gate)
            return state.Items.Count;
    }

    public int GetTurnCount(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return 0;
        lock (state.Gate)
            return state.TurnCount;
    }

    public void SetTurnCount(string sessionId, int turns)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        lock (state.Gate)
            state.TurnCount = Math.Max(0, turns);
    }

    public void Requeue(string sessionId, IReadOnlyList<MemoryCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || candidates.Count == 0)
            return;

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState());
        lock (state.Gate)
        {
            state.Items.InsertRange(0, candidates);
            state.TotalChars += candidates.Sum(c => c.Snippet.Length);
            state.LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private static MemoryBatch? TakeLocked(string sessionId, SessionState state)
    {
        if (state.Items.Count == 0)
            return null;

        var items = state.Items.ToList();
        state.Items.Clear();
        state.TotalChars = 0;
        return new MemoryBatch(
            Guid.NewGuid().ToString("N"),
            sessionId,
            items,
            DateTimeOffset.UtcNow);
    }

    private sealed class SessionState
    {
        public object Gate { get; } = new();
        public List<MemoryCandidate> Items { get; } = new();
        public int TotalChars { get; set; }
        public int TurnCount { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
