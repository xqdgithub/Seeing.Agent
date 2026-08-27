using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionEventStreamRouterTests
{
    private sealed class FakeConsumer : IStreamConsumer
    {
        public string SessionId { get; }
        public List<IMessageEvent> Events { get; } = new();
        public bool StreamEnded { get; private set; }
        public FakeConsumer(string sessionId) => SessionId = sessionId;
        public void OnEvent(IMessageEvent evt) => Events.Add(evt);
        public void OnStreamEnd() => StreamEnded = true;
    }

    private static SessionEventStreamRouter CreateRouter(
        Mock<IChatOrchestrator> orchestrator, IServiceScopeFactory? scopeFactory = null)
        => new(orchestrator.Object, scopeFactory ?? Mock.Of<IServiceScopeFactory>(),
            NullLogger<SessionEventStreamRouter>.Instance);

    [Fact]
    public async Task AttachConsumer_ShouldStartLoopAndBroadcast()
    {
        var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l1" };
        var channel = Channel.CreateUnbounded<IMessageEvent>();
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());

        using var router = CreateRouter(orchestrator);
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer);

        await channel.Writer.WriteAsync(evt);
        await Task.Delay(200);
        consumer.Events.Should().ContainSingle(e => ReferenceEquals(e, evt));

        router.DetachConsumer("s1", consumer);
    }

    [Fact]
    public async Task AttachConsumer_WithReplay_ShouldDeliverBufferedEventsOnce()
    {
        var buffered = new List<IMessageEvent>
        {
            new ToolCallEvent { SessionId = "s1", Type = MessageEventType.ToolCallRunning,
                ToolCallId = "t1", ToolName = "read", Status = ToolCallStatus.Running }
        };
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents("s1")).Returns(buffered);
        var channel = Channel.CreateUnbounded<IMessageEvent>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());

        using var router = CreateRouter(orchestrator);
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer, replay: true);

        await Task.Delay(200);
        consumer.Events.Should().ContainSingle(); // buffer 补历史一次（replay）
    }

    [Fact]
    public async Task AttachConsumer_SkipSet_ShouldNotRedeliverBufferedEvents()
    {
        var buffered = new List<IMessageEvent>
        {
            new LoopStartEvent { SessionId = "s1", LoopId = "old" }
        };
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents("s1")).Returns(buffered);
        var channel = Channel.CreateUnbounded<IMessageEvent>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());

        using var router = CreateRouter(orchestrator);
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer); // 非 replay：skipSet 丢弃 buffer 历史

        await Task.Delay(200);
        consumer.Events.Should().BeEmpty(); // buffer 历史被 skip

        var live = new LoopStartEvent { SessionId = "s1", LoopId = "new" };
        await channel.Writer.WriteAsync(live);
        await Task.Delay(200);
        consumer.Events.Should().ContainSingle(e => ReferenceEquals(e, live));
    }

    [Fact]
    public void DetachAllForCircuit_ShouldDisposeScopes()
    {
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(It.IsAny<Type>())).Returns(() => new FakeConsumer("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);
        router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");

        router.DetachAllForCircuit("circuit-1");
        scope.Verify(s => s.Dispose(), Times.Once);
    }
}
