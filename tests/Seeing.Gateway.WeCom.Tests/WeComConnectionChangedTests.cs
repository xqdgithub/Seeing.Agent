using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComConnectionChangedTests
{
    [Fact]
    public async Task PauseAllAsync_ShouldNotifyDegradedWithoutAborting()
    {
        var registry = new WeComActiveStreamRegistry();
        var handle = new TrackingStreamHandle();
        registry.Register("stream_1", handle);

        await registry.PauseAllAsync(CancellationToken.None);

        handle.DegradedCount.Should().Be(1);
        handle.AbortCount.Should().Be(0);
    }

    [Fact]
    public async Task FlushAllAsync_ShouldNotifyAllStreams()
    {
        var registry = new WeComActiveStreamRegistry();
        var handle = new TrackingStreamHandle();
        registry.Register("stream_1", handle);

        await registry.FlushAllAsync(CancellationToken.None);

        handle.FlushCount.Should().Be(1);
    }

    [Fact]
    public async Task AbortAllAsync_ShouldAbortAndClearRegistry()
    {
        var registry = new WeComActiveStreamRegistry();
        var handle = new TrackingStreamHandle();
        registry.Register("stream_1", handle);

        await registry.AbortAllAsync("fatal", CancellationToken.None);

        handle.AbortCount.Should().Be(1);
        registry.TryHandleRefresh(new WeComWsFrame(), "stream_1", CancellationToken.None).Should().BeFalse();
    }

    private sealed class TrackingStreamHandle : IWeComActiveStreamHandle
    {
        public int DegradedCount { get; private set; }

        public int FlushCount { get; private set; }

        public int AbortCount { get; private set; }

        public Task HandleRefreshAsync(WeComWsFrame refreshFrame, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task NotifyConnectionDegradedAsync(CancellationToken cancellationToken)
        {
            DegradedCount++;
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public Task AbortAsync(string reason, CancellationToken cancellationToken)
        {
            AbortCount++;
            return Task.CompletedTask;
        }
    }
}
