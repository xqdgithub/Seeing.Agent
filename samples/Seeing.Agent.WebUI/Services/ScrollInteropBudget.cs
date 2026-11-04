namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 一次渲染通道内滚动互操作的放行结果。
/// </summary>
/// <param name="AllowScroll">是否允许执行贴底判定（<c>shouldAutoScroll</c> / <c>notifyContentGrew</c>）。</param>
/// <param name="AllowLoadMore">是否允许执行懒加载判定（<c>isNearTop</c> / <c>getScrollHeight</c>）。</param>
public readonly record struct ScrollInteropGrant(bool AllowScroll, bool AllowLoadMore);

/// <summary>
/// 滚动互操作限流窗口的分配器。
/// <para>
/// 背景：贴底判定与懒加载判定都在 <c>OnAfterRenderAsync</c> 里，若各自独立调用
/// <see cref="ScrollInteropThrottle.ShouldInvoke"/>，先执行的那一步会消费掉窗口，
/// 后执行的一步必然被拒 —— 长会话（<c>_windowStart &gt; 0</c>）的「向上加载更早消息」会
/// 永久无法触发。本类保证：**同一个渲染通道内，两个判定共享一次窗口判定结果**，
/// 且只在实际需要时才消费窗口。
/// </para>
/// <para>非线程安全：调用方保证在 Blazor Dispatcher 线程使用。</para>
/// </summary>
public static class ScrollInteropBudget
{
    /// <summary>
    /// 为本次渲染分配放行结果。
    /// <para>
    /// 规则：若贴底与懒加载都无事可做，<b>不消费</b>窗口；只要有一项需要执行，
    /// 就按窗口判定一次，并把该次结果同时授予两项（避免互相饿死）。
    /// </para>
    /// </summary>
    /// <param name="throttle">限流器（共享实例）。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <param name="scrollNeeded">本次是否真的需要做贴底判定（内容增长 / 强制贴底）。</param>
    /// <param name="loadMorePossible">本次是否真的需要做懒加载判定（还有更早消息可加载）。</param>
    public static ScrollInteropGrant Allocate(
        ScrollInteropThrottle throttle,
        DateTime now,
        bool scrollNeeded,
        bool loadMorePossible)
    {
        if (throttle is null)
            throw new ArgumentNullException(nameof(throttle));

        // 两者都无事可做：保持窗口完好，供下一次真正需要时使用
        if (!scrollNeeded && !loadMorePossible)
            return new ScrollInteropGrant(false, false);

        var allowed = throttle.ShouldInvoke(now);
        return new ScrollInteropGrant(
            AllowScroll: allowed && scrollNeeded,
            AllowLoadMore: allowed && loadMorePossible);
    }
}
