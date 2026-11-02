using System.Collections.Concurrent;
using Seeing.Session.Core;

namespace Seeing.Session.Management;

/// <summary>
/// <see cref="ISessionActivityTracker"/> 的进程内实现：单纯的时间戳字典。
/// <para>
/// 有意不引入过期清理线程——<see cref="Clear"/> 由会话删除路径负责调用；
/// 后台扫描自身只读快照，不改变生命周期。这样保持零后台资源。
/// </para>
/// </summary>
public sealed class SessionActivityTracker : ISessionActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Touch(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _last[sessionId] = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetIdleSessions(TimeSpan idle)
    {
        var cutoff = DateTimeOffset.UtcNow - idle;
        return _last
            .Where(pair => pair.Value <= cutoff)
            .Select(pair => pair.Key)
            .ToList();
    }

    /// <inheritdoc />
    public void Clear(string sessionId) => _last.TryRemove(sessionId, out _);

    /// <inheritdoc />
    public bool TryGetLastActivity(string sessionId, out DateTimeOffset lastActivity)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            lastActivity = default;
            return false;
        }

        return _last.TryGetValue(sessionId, out lastActivity);
    }
}
