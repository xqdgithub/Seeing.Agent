namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 滚动互操作限流器：限制相邻两次 JS 互操作的最小间隔。
/// <para>
/// 消息列表每次渲染后的滚动判定都要往返 JS；与高频渲染叠加时会形成「渲染—往返」串行排队，
/// 表现为流式输出发涩。经限流后 JS 调用次数与渲染次数解耦。
/// </para>
/// <para><b>非线程安全</b>：调用方保证在 Blazor Dispatcher 线程使用。</para>
/// </summary>
public sealed class ScrollInteropThrottle
{
    private readonly TimeSpan _minInterval;
    private DateTime? _lastInvokedAt;

    public ScrollInteropThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    /// <summary>若距上次放行已超过最小间隔则放行并记录，否则返回 <c>false</c>。</summary>
    public bool ShouldInvoke(DateTime now)
    {
        if (_lastInvokedAt is DateTime last && now - last < _minInterval)
            return false;

        _lastInvokedAt = now;
        return true;
    }

    /// <summary>重置限流窗口（强制贴底、会话切换等需要立即判定的场景）。</summary>
    public void Reset() => _lastInvokedAt = null;
}
