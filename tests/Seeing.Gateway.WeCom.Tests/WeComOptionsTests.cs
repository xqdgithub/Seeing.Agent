using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComOptionsTests
{
    [Fact]
    public void EffectiveHeartbeatIntervalSeconds_ShouldDefaultTo30WhenZero()
    {
        var options = new WeComOptions { HeartbeatIntervalSeconds = 0 };

        options.EffectiveHeartbeatIntervalSeconds.Should().Be(30);
    }

    [Fact]
    public void EffectiveDeltaThrottleMilliseconds_ShouldDefaultTo150WhenZero()
    {
        var options = new WeComOptions { DeltaThrottleMilliseconds = 0 };

        options.EffectiveDeltaThrottleMilliseconds.Should().Be(150);
    }

    [Fact]
    public void EffectiveDeltaThrottleMilliseconds_ShouldUseConfiguredValue()
    {
        var options = new WeComOptions { DeltaThrottleMilliseconds = 500 };

        options.EffectiveDeltaThrottleMilliseconds.Should().Be(500);
    }
}
