using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public sealed class GatewayRunTrackerTests
{
    [Fact]
    public void RegisterRun_ThreeSubscriptions_ShouldNotReplaceEachOther()
    {
        var tracker = new GatewayRunTracker();

        var (id1, token1) = tracker.RegisterRun("ses");
        var (id2, token2) = tracker.RegisterRun("ses");
        var (id3, token3) = tracker.RegisterRun("ses");

        id1.Should().NotBe(id2);
        id2.Should().NotBe(id3);
        token1.IsCancellationRequested.Should().BeFalse();
        token2.IsCancellationRequested.Should().BeFalse();
        token3.IsCancellationRequested.Should().BeFalse();
        tracker.IsRunning("ses").Should().BeTrue();

        tracker.CancelRun("ses").Should().BeTrue();

        token1.IsCancellationRequested.Should().BeTrue();
        token2.IsCancellationRequested.Should().BeTrue();
        token3.IsCancellationRequested.Should().BeTrue();

        tracker.UnregisterRun("ses", id1);
        tracker.UnregisterRun("ses", id2);
        tracker.UnregisterRun("ses", id3);
        tracker.IsRunning("ses").Should().BeFalse();
    }

    [Fact]
    public void UnregisterRun_ShouldRemoveOnlyThatSubscription()
    {
        var tracker = new GatewayRunTracker();
        var (id1, token1) = tracker.RegisterRun("ses");
        var (id2, token2) = tracker.RegisterRun("ses");
        var (id3, token3) = tracker.RegisterRun("ses");

        tracker.UnregisterRun("ses", id2);

        tracker.IsRunning("ses").Should().BeTrue();
        token1.IsCancellationRequested.Should().BeFalse();
        token3.IsCancellationRequested.Should().BeFalse();

        tracker.CancelRun("ses").Should().BeTrue();
        token1.IsCancellationRequested.Should().BeTrue();
        token3.IsCancellationRequested.Should().BeTrue();

        tracker.UnregisterRun("ses", id1);
        tracker.UnregisterRun("ses", id3);
        tracker.IsRunning("ses").Should().BeFalse();
    }

    [Fact]
    public void CancelRun_ShouldCancelEveryInFlightSubscription()
    {
        var tracker = new GatewayRunTracker();
        var (leftId, left) = tracker.RegisterRun("left");
        var (rightId, right) = tracker.RegisterRun("right");

        tracker.CancelRun("left").Should().BeTrue();

        left.IsCancellationRequested.Should().BeTrue();
        right.IsCancellationRequested.Should().BeFalse();
        tracker.CancelRun("missing").Should().BeFalse();

        tracker.UnregisterRun("left", leftId);
        tracker.UnregisterRun("right", rightId);
    }
}
