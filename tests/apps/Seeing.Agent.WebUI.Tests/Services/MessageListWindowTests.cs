using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageListWindowTests
{
    [Fact]
    public void InitialStart_WhenFewerThanWindow_ShouldBeZero()
    {
        MessageListWindow.ComputeInitialStart(total: 10, initialWindow: 40)
            .Should().Be(0);
    }

    [Fact]
    public void InitialStart_WhenMoreThanWindow_ShouldPinTail()
    {
        MessageListWindow.ComputeInitialStart(total: 100, initialWindow: 40)
            .Should().Be(60);
    }

    [Fact]
    public void SlideWhilePinned_ShouldEnforceMaxMounted()
    {
        // mounted = 120 - 20 = 100 > 80 → slide to total - maxMounted
        var start = MessageListWindow.SlideWhilePinned(
            currentStart: 20, total: 120, maxMounted: 80);
        start.Should().Be(40);
    }

    [Fact]
    public void SlideWhilePinned_WhenUnderMax_ShouldKeepStart()
    {
        MessageListWindow.SlideWhilePinned(50, total: 100, maxMounted: 80)
            .Should().Be(50);
    }

    [Fact]
    public void LoadMore_ShouldDecreaseStartByBatch()
    {
        MessageListWindow.LoadMore(currentStart: 40, batch: 20)
            .Should().Be(20);
    }

    [Fact]
    public void LoadMore_ShouldNotGoBelowZero()
    {
        MessageListWindow.LoadMore(currentStart: 10, batch: 20)
            .Should().Be(0);
    }
}
