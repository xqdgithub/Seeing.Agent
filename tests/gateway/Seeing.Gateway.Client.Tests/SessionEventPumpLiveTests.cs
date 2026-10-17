using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Gateway.Models;
using System.Threading.Channels;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public sealed class SessionEventPumpLiveTests
{
    [Fact]
    public async Task BeginLive_EventsAfterRegistration_ShouldStreamWithoutReplay()
    {
        var subscription = new FakeLiveSubscription();
        var runtime = CreateRuntime();
        var (presentation, pump) = CreatePump(runtime);

        using var stream = pump.BeginLive("ses", subscription);

        // 模拟投递（唤醒）触发后发布的一轮完整事件
        subscription.Publish(new LoopStartEvent { SessionId = "ses", LoopId = "l1", UserInput = "hi" });
        subscription.Publish(new StreamDeltaEvent { SessionId = "ses", LoopId = "l1", ContentDelta = "reply" });
        subscription.Publish(new LoopCompleteEvent { SessionId = "ses", LoopId = "l1", Success = true });
        subscription.Complete();

        var received = new List<GatewayEvent>();
        await foreach (var gatewayEvent in stream.StreamAsync(TestContext.Current.CancellationToken))
            received.Add(gatewayEvent);

        received.Select(e => e.SourceType).Should().Equal(
            MessageEventType.LoopStart,
            MessageEventType.StreamDelta,
            MessageEventType.LoopComplete);
        received[0].Data!.UserInput.Should().Be("hi");

        presentation.Verify(p => p.Unregister(It.IsAny<GatewaySubscriptionPresenter>()), Times.Once);
    }

    [Fact]
    public async Task BeginLive_StreamAsyncEnd_ShouldDisposeSubscription()
    {
        var subscription = new FakeLiveSubscription();
        var runtime = CreateRuntime();
        var (_, pump) = CreatePump(runtime);

        var stream = pump.BeginLive("ses", subscription);
        subscription.Complete();

        var count = 0;
        await foreach (var _ in stream.StreamAsync(TestContext.Current.CancellationToken))
            count++;

        count.Should().Be(0);
        subscription.Disposed.Should().BeTrue();
    }

    [Fact]
    public void BeginLive_WithoutStreaming_DisposeShouldUnregisterEverything()
    {
        var subscription = new FakeLiveSubscription();
        var runtime = CreateRuntime();
        var (presentation, pump) = CreatePump(runtime);

        var stream = pump.BeginLive("ses", subscription);
        stream.Dispose();
        // 重复 Dispose 安全
        stream.Dispose();

        subscription.Disposed.Should().BeTrue();
        presentation.Verify(p => p.Unregister(It.IsAny<GatewaySubscriptionPresenter>()), Times.Once);
    }

    private static Mock<ISessionRuntime> CreateRuntime()
    {
        var runtime = new Mock<ISessionRuntime>();
        runtime
            .Setup(r => r.WaitForIdleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        runtime
            .Setup(r => r.DeliverAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InboxResult.Ok());
        return runtime;
    }

    private static (Mock<IPermissionSurfaceRegistry> Presentation, SessionEventPump Pump) CreatePump(
        Mock<ISessionRuntime> runtime)
    {
        var presentation = new Mock<IPermissionSurfaceRegistry>();
        var pump = new SessionEventPump(
            runtime.Object,
            new GatewayRunTracker(),
            presentation.Object,
            NullLogger.Instance);
        return (presentation, pump);
    }

    private sealed class FakeLiveSubscription : ILiveEventSubscription
    {
        private readonly Channel<IMessageEvent> _channel = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public bool Disposed { get; private set; }

        public void Publish(IMessageEvent evt) => _channel.Writer.TryWrite(evt);

        public void Complete() => _channel.Writer.TryComplete();

        public IAsyncEnumerator<IMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        public void Dispose()
        {
            Disposed = true;
            _channel.Writer.TryComplete();
        }
    }
}
