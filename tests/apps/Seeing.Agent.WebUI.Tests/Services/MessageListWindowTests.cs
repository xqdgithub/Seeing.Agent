using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>消息列表窗口裁剪：首屏、贴底滑动、向上懒加载。</summary>
public class MessageListWindowTests
{
    [Fact]
    public void ComputeInitialStart_Empty_ShouldBeZero()
        => MessageListWindow.ComputeInitialStart(0).Should().Be(0);

    [Fact]
    public void ComputeInitialStart_FewerThanWindow_ShouldRenderAll()
    {
        var total = MessageListWindow.InitialWindowSize - 1;

        MessageListWindow.ComputeInitialStart(total).Should().Be(0);
    }

    [Fact]
    public void ComputeInitialStart_ExactlyWindow_ShouldRenderAll()
        => MessageListWindow.ComputeInitialStart(MessageListWindow.InitialWindowSize).Should().Be(0);

    [Fact]
    public void ComputeInitialStart_MoreThanWindow_ShouldKeepTail()
    {
        var total = 500;

        var start = MessageListWindow.ComputeInitialStart(total);

        start.Should().Be(total - MessageListWindow.InitialWindowSize);
        (total - start).Should().Be(MessageListWindow.InitialWindowSize);
    }

    [Fact]
    public void SlideWhilePinned_BelowLimit_ShouldNotSlide()
    {
        var total = MessageListWindow.MaxMounted + 10;
        var start = MessageListWindow.MaxMounted - 10; // 挂载 20 条 < 上限 → 不动

        MessageListWindow.SlideWhilePinned(start, total).Should().Be(start);
    }

    [Fact]
    public void SlideWhilePinned_ExactlyAtLimit_ShouldNotSlide()
    {
        var total = MessageListWindow.MaxMounted + 10;
        var start = 10; // 挂载恰好 MaxMounted

        MessageListWindow.SlideWhilePinned(start, total).Should().Be(start);
    }

    [Fact]
    public void SlideWhilePinned_AboveLimit_ShouldTrimToMaxMounted()
    {
        var total = MessageListWindow.MaxMounted * 3;

        var start = MessageListWindow.SlideWhilePinned(0, total);

        (total - start).Should().Be(MessageListWindow.MaxMounted);
    }

    /// <summary>贴底滑动后挂载数必须恰好等于上限，这是 Paint 成本的直接约束。</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(5000)]
    public void SlideWhilePinned_LargeTotals_ShouldMountExactlyMaxMounted(int total)
    {
        var start = MessageListWindow.SlideWhilePinned(0, total);

        (total - start).Should().Be(MessageListWindow.MaxMounted);
    }

    [Fact]
    public void SlideWhilePinned_NeverNegativeStart()
        => MessageListWindow.SlideWhilePinned(0, 5).Should().Be(0);

    [Fact]
    public void LoadMore_ShouldSubtractBatch()
    {
        var batch = MessageListWindow.LoadMoreBatch;
        MessageListWindow.LoadMore(batch * 2).Should().Be(batch);
        MessageListWindow.LoadMore(batch).Should().Be(0);
    }

    [Fact]
    public void LoadMore_NeverNegative()
        => MessageListWindow.LoadMore(1).Should().Be(0);

    /// <summary>
    /// 契约：首屏窗口与挂载上限必须一致。二者若不等，进入会话后第一次贴底滑动
    /// 会立即裁掉刚渲染的条目，造成可见跳动。
    /// </summary>
    [Fact]
    public void InitialWindowSize_ShouldMatchMaxMounted()
        => MessageListWindow.InitialWindowSize.Should().Be(MessageListWindow.MaxMounted);

    /// <summary>
    /// 契约：挂载上限是浏览器 Paint 成本的直接约束（实测 80 条时 Paint 1816ms）。
    /// 上调此值前请重新测量切换与流式输出期间的长任务耗时。
    /// </summary>
    [Fact]
    public void MaxMounted_ShouldStayAtOrBelowDocumentedThreshold()
        => MessageListWindow.MaxMounted.Should().BeLessThanOrEqualTo(40);
}
