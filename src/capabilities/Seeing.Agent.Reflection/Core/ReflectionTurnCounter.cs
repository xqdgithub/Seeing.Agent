using System.Collections.Concurrent;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 会话复盘轮次计数器（进程内，有界）。
/// <para>
/// 仅用于决定「是否到点触发复盘」，不做持久化：重启后归零是可接受的
/// （复盘不是精确计费，最多让某个会话的触发点后移几轮）。
/// </para>
/// <para>
/// <b>有界是必需的</b>：<see cref="Increment"/> 会为每个观察到终态的会话建条目，
/// 而会话删除路径在 primitives 层、不该反向依赖本包，因此没有天然的清理时机。
/// 不设上限时字典会随进程生命周期里的会话总数无界增长。
/// 超限时淘汰<b>最久未被触发</b>的条目——被淘汰的会话轮次归零，
/// 与「重启归零可接受」是同一类代价。
/// </para>
/// </summary>
public sealed class ReflectionTurnCounter
{
    /// <summary>条目数上限。</summary>
    /// <remarks>
    /// 512 的取法：单用户同时活跃的会话通常在 20 以内，512 留出约 25 倍余量，
    /// 正常使用下永不触发淘汰；内存占用约几十 KB。
    /// </remarks>
    public const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Entry> _counts = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly TimeProvider _time;

    /// <summary>
    /// 单调递增的触发序号。淘汰按「最久未触发」排序，同一时间戳会让顺序不确定；
    /// 用序号兜底，保证淘汰结果与真实调用顺序一致（也让虚拟时钟下的测试可确定）。
    /// </summary>
    private long _sequence;

    /// <summary>以默认上限创建计数器。</summary>
    public ReflectionTurnCounter() : this(MaxEntries)
    {
    }

    /// <summary>以默认上限 + 注入时钟创建（测试用虚拟时间）。</summary>
    /// <param name="timeProvider">时钟。</param>
    public ReflectionTurnCounter(TimeProvider timeProvider) : this(MaxEntries, timeProvider)
    {
    }

    /// <summary>以指定上限创建计数器（测试用）。</summary>
    /// <param name="maxEntries">条目数上限，必须为正。</param>
    public ReflectionTurnCounter(int maxEntries)
        : this(maxEntries, TimeProvider.System)
    {
    }

    /// <summary>以指定上限 + 注入时钟创建。</summary>
    /// <param name="maxEntries">条目数上限，必须为正。</param>
    /// <param name="timeProvider">时钟。</param>
    public ReflectionTurnCounter(int maxEntries, TimeProvider timeProvider)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "容量上限必须为正数。");

        _maxEntries = maxEntries;
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>当前条目数（用于淘汰断言）。</summary>
    public int Count => _counts.Count;

    /// <summary>会话轮次 +1 并返回累加后的值。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public int Increment(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        var now = _time.GetUtcNow();
        var seq = Interlocked.Increment(ref _sequence);
        var updated = _counts.AddOrUpdate(
            sessionId,
            _ => new Entry(1, now, seq),
            (_, current) => new Entry(current.Count + 1, now, seq));

        // 只在超限时才做淘汰扫描，正常路径零额外开销。
        if (_counts.Count > _maxEntries)
            EvictLeastRecentlyUsed();

        return updated.Count;
    }

    /// <summary>将计数清零但保留键（下一次触发重新开始累计）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public void Reset(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        // 保留上一次触发时刻：Reset 表示「刚触发过」，不是「没活动」。
        _counts.AddOrUpdate(
            sessionId,
            _ => new Entry(0, _time.GetUtcNow(), Interlocked.Increment(ref _sequence)),
            (_, current) => new Entry(0, current.LastTouchedAt, current.Sequence));
    }

    /// <summary>读取当前计数（无记录时为 0）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public int GetCount(string sessionId)
        => string.IsNullOrWhiteSpace(sessionId)
            ? 0
            : _counts.TryGetValue(sessionId, out var entry) ? entry.Count : 0;

    /// <summary>彻底移除该会话的计数（会话被删除/清理时调用）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        _counts.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 淘汰最久未被触发的条目，直到条目数回落到上限以内。
    /// <para>
    /// 有界且并发安全：即使两个线程同时淘汰，最终也只会多淘汰几条——
    /// 代价是某会话触发点后移几轮，不是正确性问题。
    /// </para>
    /// </summary>
    private void EvictLeastRecentlyUsed()
    {
        var excess = _counts.Count - _maxEntries;
        if (excess <= 0)
            return;

        // 按触发时刻升序取最旧的一批。会话数有界（≤ 上限 + 并发增量），排序成本可忽略。
        var victims = _counts
            .OrderBy(pair => pair.Value.LastTouchedAt)
            .ThenBy(pair => pair.Value.Sequence)
            .Take(excess)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var victim in victims)
            _counts.TryRemove(victim, out _);
    }

    /// <summary>计数 + 最近一次触发时刻。</summary>
    /// <remarks>用不可变 record struct 承载，读写都走 <c>AddOrUpdate</c> 的原子替换。</remarks>
    private readonly record struct Entry(int Count, DateTimeOffset LastTouchedAt, long Sequence);
}
