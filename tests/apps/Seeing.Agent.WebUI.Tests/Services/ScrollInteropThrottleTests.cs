using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>滚动互操作限流：窗口内只放行一次。</summary>
public class ScrollInteropThrottleTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 10, 0, 0);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    [Fact]
    public void ShouldInvoke_FirstCall_ShouldReturnTrue()
        => new ScrollInteropThrottle(Interval).ShouldInvoke(T0).Should().BeTrue();

    [Fact]
    public void ShouldInvoke_WithinInterval_ShouldReturnFalse()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.ShouldInvoke(T0.AddMilliseconds(20)).Should().BeFalse();
    }

    [Fact]
    public void ShouldInvoke_AfterInterval_ShouldReturnTrue()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.ShouldInvoke(T0.AddMilliseconds(41)).Should().BeTrue();
    }

    [Fact]
    public void Reset_ShouldAllowImmediateInvoke()
    {
        var t = new ScrollInteropThrottle(Interval);
        t.ShouldInvoke(T0);
        t.Reset();
        t.ShouldInvoke(T0.AddMilliseconds(1)).Should().BeTrue();
    }
}
