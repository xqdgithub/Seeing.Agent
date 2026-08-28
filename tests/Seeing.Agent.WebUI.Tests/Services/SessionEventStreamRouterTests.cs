using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

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

    [Fact]
    public void GetOrCreateConsumer_SameSessionDifferentCircuits_ShouldReturnDistinctInstances()
    {
        // C1：同会话不同 circuit（多标签页）各自持有独立实例与 scope，互不共享；
        // 同 (session, circuit) 幂等复用。
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(typeof(FakeConsumer))).Returns(() => new FakeConsumer("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        var circuit1 = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var circuit2 = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-2");
        var sameCircuit = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");

        circuit1.Should().NotBeSameAs(circuit2);
        sameCircuit.Should().BeSameAs(circuit1);
        scopeFactory.Verify(f => f.CreateScope(), Times.Exactly(2));
    }

    [Fact]
    public async Task DetachAllForCircuit_ShouldNotAffectOtherCircuitConsumers()
    {
        // C1：同会话双 circuit 各自独立 consumer；一方 circuit 关闭（DetachAllForCircuit）
        // 只摘除并释放其自身 consumer，另一方订阅不受影响、事件照常送达。
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(typeof(FakeConsumer))).Returns(() => new FakeConsumer("s1"));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var channel = Channel.CreateUnbounded<IMessageEvent>();
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(channel.Reader.ReadAllAsync());
        orchestrator.Setup(o => o.GetBufferedEvents("s1")).Returns(new List<IMessageEvent>());

        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        var c1 = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-1");
        var c2 = router.GetOrCreateConsumer<FakeConsumer>("s1", "circuit-2");
        router.AttachConsumer("s1", c1);
        router.AttachConsumer("s1", c2);

        // circuit-1 关闭：释放其 consumer（scope 至少释放一次）
        router.DetachAllForCircuit("circuit-1");
        scope.Verify(s => s.Dispose(), Times.AtLeastOnce);

        // circuit-2 的 c2 订阅不受影响：事件照常送达
        var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l1" };
        await channel.Writer.WriteAsync(evt);
        await Task.Delay(200);
        c2.Events.Should().Contain(e => ReferenceEquals(e, evt));
    }

    [Fact]
    public async Task AttachConsumer_AfterStreamCompleted_ShouldRestartLoopAndDeliverEvents()
    {
        // I1：会话空闲清理（CompleteSession 模拟：channel 完成）后消费 loop 自然结束、
        // Loop=null 但消费者仍挂载；再次 AttachConsumer（同页新提交重订阅）应重建 consume loop，
        // 新执行事件可送达。
        var channel1 = Channel.CreateUnbounded<IMessageEvent>();
        var channel2 = Channel.CreateUnbounded<IMessageEvent>();
        var channels = new Queue<Channel<IMessageEvent>>(new[] { channel1, channel2 });
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.SubscribeEvents("s1", It.IsAny<CancellationToken>()))
            .Returns(() => channels.Dequeue().Reader.ReadAllAsync());
        orchestrator.Setup(o => o.GetBufferedEvents("s1")).Returns(new List<IMessageEvent>());

        using var router = CreateRouter(orchestrator);
        var consumer = new FakeConsumer("s1");
        router.AttachConsumer("s1", consumer); // loop1 消费 channel1

        await Task.Delay(100);
        channel1.Writer.TryComplete(); // 模拟空闲清理：流完成
        await Task.Delay(200);
        consumer.StreamEnded.Should().BeTrue(); // loop 结束时广播流结束

        // 同页新提交：再次 AttachConsumer → 应重启 loop（消费 channel2）
        router.AttachConsumer("s1", consumer);

        var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l2" };
        await channel2.Writer.WriteAsync(evt);
        await Task.Delay(200);
        consumer.Events.Should().Contain(e => ReferenceEquals(e, evt));
        channels.Count.Should().Be(0); // 两次订阅均已建立
    }

    [Fact]
    public void GetOrCreateConsumer_Aggregator_SameCircuitDifferentSessions_ShouldReuseInstance()
    {
        // I2（方案 A）：TaskCardAggregator 为 circuit 维度消费者——同 circuit 跨会话复用同一实例
        // （页面经 Rebind 切换父会话），避免访问新会话累积聚合器实例与 scope；跨 circuit 仍隔离。
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        sp.Setup(s => s.GetService(typeof(TaskCardAggregator)))
            .Returns(() => new TaskCardAggregator(null!, Mock.Of<ISessionManager>(), new TaskSessionResolver(Mock.Of<ISessionManager>())));
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var orchestrator = new Mock<IChatOrchestrator>();
        using var router = CreateRouter(orchestrator, scopeFactory.Object);

        var a = router.GetOrCreateConsumer<TaskCardAggregator>("sessionA", "circuit-1");
        var b = router.GetOrCreateConsumer<TaskCardAggregator>("sessionB", "circuit-1");
        var c = router.GetOrCreateConsumer<TaskCardAggregator>("sessionA", "circuit-2");

        a.Should().BeSameAs(b);    // 同 circuit 跨会话复用（不累积）
        a.Should().NotBeSameAs(c); // 跨 circuit 隔离
        scopeFactory.Verify(f => f.CreateScope(), Times.Exactly(2)); // 仅 circuit-1、circuit-2 各一次
    }
}
