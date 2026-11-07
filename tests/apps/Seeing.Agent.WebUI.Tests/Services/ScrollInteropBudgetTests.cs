using FluentAssertions;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 滚动限流共享窗口的分配策略：贴底判定与懒加载必须各自拿到独立的放行机会，
/// 否则同一渲染通道内先执行的贴底会吃掉窗口，懒加载永久饿死。
/// </summary>
public class ScrollInteropBudgetTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);
    private static readonly DateTime T0 = new(2026, 11, 4, 10, 0, 0);

    [Fact]
    public void Allocate_FirstRender_ShouldAllowBoth()
    {
        var throttle = new ScrollInteropThrottle(Interval);

        var budget = ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: true, loadMorePossible: true);

        budget.AllowScroll.Should().BeTrue();
        budget.AllowLoadMore.Should().BeTrue();
    }

    [Fact]
    public void Allocate_WithinWindow_ShouldDenyBoth()
    {
        var throttle = new ScrollInteropThrottle(Interval);
        ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: true, loadMorePossible: true);

        var budget = ScrollInteropBudget.Allocate(throttle, T0.AddMilliseconds(10), scrollNeeded: true, loadMorePossible: true);

        budget.AllowScroll.Should().BeFalse();
        budget.AllowLoadMore.Should().BeFalse();
    }

    [Fact]
    public void Allocate_AfterWindow_ShouldAllowBoth()
    {
        var throttle = new ScrollInteropThrottle(Interval);
        ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: true, loadMorePossible: true);

        var budget = ScrollInteropBudget.Allocate(throttle, T0.AddMilliseconds(41), scrollNeeded: true, loadMorePossible: true);

        budget.AllowScroll.Should().BeTrue();
        budget.AllowLoadMore.Should().BeTrue();
    }

    /// <summary>Review Focus：贴底无事可做时不得消费窗口，否则懒加载被饿死。</summary>
    [Fact]
    public void Allocate_ScrollNotNeeded_ShouldNotConsumeWindow()
    {
        var throttle = new ScrollInteropThrottle(Interval);

        var first = ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: false, loadMorePossible: true);
        first.AllowScroll.Should().BeFalse();
        first.AllowLoadMore.Should().BeTrue();

        // 窗口已被懒加载消费；下一个渲染周期（过期后）仍应可放行
        var second = ScrollInteropBudget.Allocate(throttle, T0.AddMilliseconds(41), scrollNeeded: false, loadMorePossible: true);
        second.AllowLoadMore.Should().BeTrue();
    }

    /// <summary>懒加载不可用时不得消费窗口，避免贴底被反噬。</summary>
    [Fact]
    public void Allocate_LoadMoreNotPossible_ShouldNotConsumeWindow()
    {
        var throttle = new ScrollInteropThrottle(Interval);

        var first = ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: true, loadMorePossible: false);
        first.AllowScroll.Should().BeTrue();
        first.AllowLoadMore.Should().BeFalse();

        var second = ScrollInteropBudget.Allocate(throttle, T0.AddMilliseconds(41), scrollNeeded: true, loadMorePossible: false);
        second.AllowScroll.Should().BeTrue();
    }

    /// <summary>两者都需要时，只有一个消费窗口，另一个在下一窗口放行（不是永久拒绝）。</summary>
    [Fact]
    public void Allocate_BothNeeded_ShouldAlternateAcrossWindows()
    {
        var throttle = new ScrollInteropThrottle(Interval);

        var first = ScrollInteropBudget.Allocate(throttle, T0, scrollNeeded: true, loadMorePossible: true);
        var second = ScrollInteropBudget.Allocate(throttle, T0.AddMilliseconds(41), scrollNeeded: true, loadMorePossible: true);

        (first.AllowScroll && first.AllowLoadMore).Should().BeTrue();
        (second.AllowScroll && second.AllowLoadMore).Should().BeTrue();
    }
}
