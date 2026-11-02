namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 复盘执行频率限流器（进程内，1 小时滑动窗口）。
/// <para>
/// 这只是一道「别把成本打爆」的粗闸：窗口在进程内维护，重启归零。
/// 复盘不是精确计费，允许少量漂移，换取零持久化依赖与零接口复杂度。
/// </para>
/// </summary>
public sealed class ReflectionRateLimiter
{
    private static readonly TimeSpan s_window = TimeSpan.FromHours(1);

    private readonly Queue<DateTimeOffset> _acquisitions = new();
    private readonly object _gate = new();

    /// <summary>
    /// 尝试占用一次执行额度。
    /// </summary>
    /// <param name="maxPerHour">窗口内允许的最大次数；小于等于 0 表示一律拒绝。</param>
    /// <param name="now">当前时刻（由调用方注入，便于测试控制时间）。</param>
    /// <returns>占用成功为 <c>true</c>；额度已满为 <c>false</c>。</returns>
    public bool TryAcquire(int maxPerHour, DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);

            var limit = Math.Max(0, maxPerHour);
            if (_acquisitions.Count >= limit)
            {
                return false;
            }

            _acquisitions.Enqueue(now);
            return true;
        }
    }

    /// <summary>返回窗口内的已占用次数（会先清理过期条目）。</summary>
    /// <param name="now">当前时刻。</param>
    public int CountInWindow(DateTimeOffset now)
    {
        lock (_gate)
        {
            Trim(now);
            return _acquisitions.Count;
        }
    }

    /// <summary>丢弃窗口外的历史占用（恰好等于窗口长度的视为过期）。</summary>
    private void Trim(DateTimeOffset now)
    {
        while (_acquisitions.Count > 0 && now - _acquisitions.Peek() >= s_window)
        {
            _acquisitions.Dequeue();
        }
    }
}
