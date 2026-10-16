using FluentAssertions;
using Seeing.Gateway.QQ.Delivery;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQPassiveWindowTrackerTests
{
    [Fact]
    public void TryAcquire_WithinWindow_ShouldAllowPassive()
    {
        var t0 = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var now = t0;
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => now);
        tracker.RegisterInbound("m1", t0);

        var result = tracker.TryAcquirePassive("m1");

        result.CanPassive.Should().BeTrue();
        result.MustDropMsgId.Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_PastWindow_ShouldDropMsgId()
    {
        var t0 = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var now = t0;
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => now);
        tracker.RegisterInbound("m1", t0);
        now = t0.AddSeconds(301);

        var result = tracker.TryAcquirePassive("m1");

        result.CanPassive.Should().BeFalse();
        result.MustDropMsgId.Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_SameMsgIdOverLimit_ShouldDropMsgId()
    {
        var t0 = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => t0);
        tracker.RegisterInbound("m1", t0);

        tracker.TryAcquirePassive("m1").CanPassive.Should().BeTrue();
        tracker.TryAcquirePassive("m1").CanPassive.Should().BeTrue();

        var third = tracker.TryAcquirePassive("m1");
        third.CanPassive.Should().BeFalse();
        third.MustDropMsgId.Should().BeTrue();
    }
}
