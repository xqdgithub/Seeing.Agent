using System.Collections.Concurrent;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 会话复盘轮次计数器（进程内）。
/// <para>
/// 仅用于决定「是否到点触发复盘」，不做持久化：重启后归零是可接受的
/// （复盘不是精确计费，最多让某个会话的触发点后移几轮）。
/// </para>
/// </summary>
public sealed class ReflectionTurnCounter
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>会话轮次 +1 并返回累加后的值。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public int Increment(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        return _counts.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
    }

    /// <summary>将计数清零但保留键（下一次触发重新开始累计）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public void Reset(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _counts[sessionId] = 0;
    }

    /// <summary>读取当前计数（无记录时为 0）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public int GetCount(string sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? 0 : _counts.TryGetValue(sessionId, out var value) ? value : 0;

    /// <summary>彻底移除该会话的计数（会话被删除/清理时调用，避免字典无界增长）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _counts.TryRemove(sessionId, out _);
    }
}
