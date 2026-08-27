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

    private sealed class FakeConsumer2 : IStreamConsumer
    {
        public string SessionId { get; }
        public FakeConsumer2(string sessionId) => SessionId = sessionId;
        public void OnEvent(IMessageEvent evt) { }
        public void OnStreamEnd() { }
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

    [Fact]
    public void GetOrCreateConsumer_SameSessionSameCircuit_ShouldReturnSameInstance()
    {
        // C2：幂等——连续两次同 (session, circuit) 返回同一实例，且只建一个 scope（不泄漏）
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(typeof(FakeConsumer)))
            .Returns(() => new FakeConsumer("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        var first = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var second = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");

        second.Should().BeSameAs(first);
        scopeFactory.Verify(f => f.CreateScope(), Times.Once);
        scope.Verify(s => s.Dispose(), Times.Never);
    }

    [Fact]
    public void GetOrCreateConsumer_SameSessionDifferentTypes_ShouldReturnDistinctInstances()
    {
        // 同 (session, circuit) 下不同消费者类型并存（渲染 handler + TaskCardAggregator）：
        // 同类型幂等复用，不同类型各自独立 scope/实例。
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(It.IsAny<Type>())).Returns<Type>(t =>
            t == typeof(FakeConsumer)
                ? new FakeConsumer("s1")
                : new FakeConsumer2("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        var first = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var second = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var other = router.GetOrCreateConsumer<FakeConsumer2>("s1", "circuit-1");

        second.Should().BeSameAs(first);
        other.Should().NotBeSameAs(first);
        scopeFactory.Verify(f => f.CreateScope(), Times.Exactly(2));
    }

    [Fact]
    public void DetachConsumer_NonMainConsumer_ShouldNotReleaseScope()
    {
        // 辅助 consumer（非主）摘除（Rebind 切换父会话/子会话终态）只移除订阅，
        // 不释放 scope，实例可继续复用（避免 ObjectDisposedException）。
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(It.IsAny<Type>())).Returns<Type>(t =>
            t == typeof(FakeConsumer)
                ? new FakeConsumer("s1")
                : new FakeConsumer2("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        // 主 consumer：FakeConsumer；辅助 consumer：FakeConsumer2
        var main = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var helper = router.GetOrCreateConsumer<FakeConsumer2>("s1", "circuit-1");
        router.AttachConsumer("s1", main);
        router.AttachConsumer("s1", helper);

        router.DetachConsumer("s1", helper);
        // 辅助 consumer 摘除：scope 不释放（实例复用语义），主 consumer scope 仍保留
        scope.Verify(s => s.Dispose(), Times.Never);

        // 主 consumer 摘除：scope 释放
        router.DetachConsumer("s1", main);
        scope.Verify(s => s.Dispose(), Times.Once);
    }
}
