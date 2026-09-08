using System.Collections.Concurrent;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

public sealed class SessionActivityTracker : ISessionActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();

    public void Touch(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        _last[sessionId] = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<string> GetIdleSessions(TimeSpan idle)
    {
        var cutoff = DateTimeOffset.UtcNow - idle;
        return _last.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
    }

    public void Clear(string sessionId) => _last.TryRemove(sessionId, out _);
}
