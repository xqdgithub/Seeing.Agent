using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComReconnectTests
{
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, -1)]
    [InlineData(3, 3)]
    public void NormalizeMaxReconnectAttempts_ShouldTreatZeroAsUnlimited(int configured, int expected)
    {
        WeComConnectionManager.NormalizeMaxReconnectAttempts(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 30)]
    [InlineData(45, 45)]
    public void ResolveHeartbeatSeconds_ShouldUseDefaultWhenZero(int configured, int expected)
    {
        WeComConnectionManager.ResolveHeartbeatSeconds(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(10, 30)]
    public void CalculateBackoffDelay_ShouldRespectMinAndMax(int attempt, int expectedSeconds)
    {
        WeComConnectionManager.CalculateBackoffDelay(attempt).TotalSeconds.Should().Be(expectedSeconds);
    }
}
