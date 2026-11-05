using System.Collections.Concurrent;
using Seeing.Session.Core;

namespace Seeing.Session.Management;

/// <summary>
/// <see cref="ISessionActivityTracker"/> 的进程内实现：单纯的时间戳字典。
/// <para>
/// 有意不引入过期清理线程——<see cref="Clear"/> 由会话删除路径负责调用；
/// 后台扫描自身只读快照，不改变生命周期。这样保持零后台资源。
/// </para>
/// <para>
/// 时钟经 <see cref="TimeProvider"/> 注入：测试可用虚拟时间推进「过去多久」，
/// 无需 <c>Thread.Sleep</c> 制造时间差（见 AGENTS.md「测试禁止等待型断言」）。
/// </para>
/// </summary>
public sealed class SessionActivityTracker : ISessionActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>使用系统时钟构造。</summary>
    public SessionActivityTracker()
        : this(TimeProvider.System)
    {
    }

    /// <summary>注入时钟（测试用虚拟时间）。</summary>
    public SessionActivityTracker(TimeProvider timeProvider)
    {
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public void Touch(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _last[sessionId] = _time.GetUtcNow();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetIdleSessions(TimeSpan idle)
    {
        var cutoff = _time.GetUtcNow() - idle;
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
