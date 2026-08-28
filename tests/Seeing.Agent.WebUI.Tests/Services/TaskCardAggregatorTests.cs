using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class TaskCardAggregatorTests
{
    private static SessionEventStreamRouter CreateRouter(Mock<IChatOrchestrator> orchestrator)
        => new(orchestrator.Object, Mock.Of<IServiceScopeFactory>(),
            NullLogger<SessionEventStreamRouter>.Instance);

    private static SessionData CreateParentWithTaskCall(string parentId, string toolCallId)
    {
        var parent = SessionData.Create("p1", "general");
        parent.Id = parentId;
        var msg = SessionMessage.AssistantMessage("thinking");
        var tc = new SessionToolCall { Id = toolCallId, Name = "task", Status = "running" };
        msg.ToolCalls = new List<SessionToolCall> { tc };
        parent.AddMessage(msg);
        return parent;
    }

    private static SessionData CreateChild(string childId, string parentId, string originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parentId;
        child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    private static Mock<ISessionManager> CreateSessionManagerMock(SessionData parent, SessionData child)
    {
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.Get(parent.Id)).Returns(parent);
        sm.Setup(m => m.Get(child.Id)).Returns(child);
        sm.Setup(m => m.ListChildrenAsync(parent.Id, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parent.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        return sm;
    }

    private static Mock<IChatOrchestrator> CreateOrchestratorMock(
        Dictionary<string, Channel<IMessageEvent>> channels)
    {
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents(It.IsAny<string>())).Returns(new List<IMessageEvent>());
        orchestrator.Setup(o => o.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string sessionId, CancellationToken _) => channels[sessionId].Reader.ReadAllAsync());
        return orchestrator;
    }

    [Fact]
    public async Task OnEvent_ChildToolCall_ShouldAggregateTaskSteps()
    {
        var parentId = "parent1";
        var childId = "child1";
        var parent = CreateParentWithTaskCall(parentId, "call-1");
        var child = CreateChild(childId, parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var childChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = CreateSessionManagerMock(parent, child);
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [childId] = childChannel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        var assistantChanged = false;
        aggregator.AssistantChanged += _ => assistantChanged = true;
        aggregator.Rebind(parentId); // 订阅父流

        // 父流 task 工具调用 → 挂载子流
        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });

        await Task.Delay(200);
        // 子流工具事件 → 聚合 TaskSteps
        await childChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = childId, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct1", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
        });
        await Task.Delay(300);

        var toolCall = parent.Messages[0].ToolCalls[0];
        toolCall.TaskId.Should().Be(childId);
        toolCall.TaskSteps.Should().ContainSingle(s => s.ToolCallId == "ct1" && s.ToolName == "read");
        assistantChanged.Should().BeTrue();

        parentChannel.Writer.TryComplete();
        childChannel.Writer.TryComplete();
    }

    [Fact]
    public async Task OnEvent_ChildTerminalEvent_ShouldStopAggregating()
    {
        var parentId = "parent1";
        var childId = "child1";
        var parent = CreateParentWithTaskCall(parentId, "call-1");
        var child = CreateChild(childId, parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var childChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = CreateSessionManagerMock(parent, child);
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [childId] = childChannel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        aggregator.Rebind(parentId);

        // 父流 task 工具调用 → 挂载子流
        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);

        // 子流事件 → 一条步骤
        await childChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = childId, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct1", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
        });
        await Task.Delay(200);

        var toolCall = parent.Messages[0].ToolCalls[0];
        toolCall.TaskSteps.Should().ContainSingle();

        // 终态 → 停止订阅
        await childChannel.Writer.WriteAsync(new ExecutionCompleteEvent { SessionId = childId });
        await Task.Delay(200);

        // 后续子流事件不再聚合
        await childChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = childId, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct2", ToolName = "write", Status = ToolCallStatus.Success, Output = "x"
        });
        await Task.Delay(200);

        toolCall.TaskSteps.Should().ContainSingle();

        parentChannel.Writer.TryComplete();
        childChannel.Writer.TryComplete();
    }

    [Fact]
    public async Task OnEvent_TwoParallelChildStreams_ShouldKeepBothSteps()
    {
        // C1：两个子会话并行事件，聚合不得互相覆盖丢步骤
        var parentId = "parent1";
        var parent = SessionData.Create("p1", "general");
        parent.Id = parentId;
        var msg = SessionMessage.AssistantMessage("thinking");
        var tc1 = new SessionToolCall { Id = "call-1", Name = "task", Status = "running" };
        var tc2 = new SessionToolCall { Id = "call-2", Name = "task", Status = "running" };
        msg.ToolCalls = new List<SessionToolCall> { tc1, tc2 };
        parent.AddMessage(msg);

        var child1 = CreateChild("child1", parentId, "call-1");
        var child2 = CreateChild("child2", parentId, "call-2");

        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.Get(parentId)).Returns(parent);
        sm.Setup(m => m.Get(child1.Id)).Returns(child1);
        sm.Setup(m => m.Get(child2.Id)).Returns(child2);
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child1, child2 }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));

        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var child1Channel = Channel.CreateUnbounded<IMessageEvent>();
        var child2Channel = Channel.CreateUnbounded<IMessageEvent>();
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [child1.Id] = child1Channel,
            [child2.Id] = child2Channel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        aggregator.Rebind(parentId);

        // 父流挂载两个子流
        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-2", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(300);

        // 两个子流并行工具事件
        await child1Channel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = child1.Id, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct1", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
        });
        await child2Channel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = child2.Id, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct2", ToolName = "write", Status = ToolCallStatus.Success, Output = "ok"
        });
        await Task.Delay(300);

        tc1.TaskId.Should().Be(child1.Id);
        tc2.TaskId.Should().Be(child2.Id);
        tc1.TaskSteps.Should().ContainSingle(s => s.ToolCallId == "ct1" && s.ToolName == "read");
        tc2.TaskSteps.Should().ContainSingle(s => s.ToolCallId == "ct2" && s.ToolName == "write");

        parentChannel.Writer.TryComplete();
        child1Channel.Writer.TryComplete();
        child2Channel.Writer.TryComplete();
    }

    [Fact]
    public async Task OnEvent_ConcurrentChildMerges_ShouldNotLoseSteps()
    {
        // C1：同一子流并发投递多个事件，读-改-写串行化后不丢步骤
        var parentId = "parent1";
        var childId = "child1";
        var parent = CreateParentWithTaskCall(parentId, "call-1");
        var child = CreateChild(childId, parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var childChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = CreateSessionManagerMock(parent, child);
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [childId] = childChannel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        aggregator.Rebind(parentId);

        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);

        const int count = 100;
        var events = Enumerable.Range(0, count)
            .Select(i => new ToolCallEvent
            {
                SessionId = childId, Type = MessageEventType.ToolCallComplete,
                ToolCallId = $"ct{i}", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
            })
            .ToArray();

        await Task.WhenAll(events.Select(e => Task.Run(() => aggregator.OnEvent(e))));

        var toolCall = parent.Messages[0].ToolCalls[0];
        toolCall.TaskSteps.Should().HaveCount(count);
        toolCall.TaskSteps!.Select(s => s.ToolCallId).Distinct().Should().HaveCount(count);

        parentChannel.Writer.TryComplete();
        childChannel.Writer.TryComplete();
    }

    [Fact]
    public async Task Rebind_ShouldDetachOldParentSubscription()
    {
        // I2：Rebind 后旧父流事件不再送达聚合器（旧父会话订阅已摘除）
        var parent1 = "parent1";
        var parent2 = "parent2";
        var parent1Data = SessionData.Create("p1", "general");
        parent1Data.Id = parent1;
        var msg = SessionMessage.AssistantMessage("thinking");
        var tc1 = new SessionToolCall { Id = "call-1", Name = "task", Status = "running" };
        var tc3 = new SessionToolCall { Id = "call-3", Name = "task", Status = "running" };
        msg.ToolCalls = new List<SessionToolCall> { tc1, tc3 };
        parent1Data.AddMessage(msg);

        var child1 = CreateChild("child1", parent1, "call-1");
        var child3 = CreateChild("child3", parent1, "call-3");
        var parent2Data = SessionData.Create("p1", "general");
        parent2Data.Id = parent2;

        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.Get(parent1)).Returns(parent1Data);
        sm.Setup(m => m.Get(parent2)).Returns(parent2Data);
        sm.Setup(m => m.Get(child1.Id)).Returns(child1);
        sm.Setup(m => m.Get(child3.Id)).Returns(child3);
        sm.Setup(m => m.ListChildrenAsync(parent1, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child1, child3 }));
        sm.Setup(m => m.ListChildrenAsync(parent2, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));

        var parent1Channel = Channel.CreateUnbounded<IMessageEvent>();
        var parent2Channel = Channel.CreateUnbounded<IMessageEvent>();
        var child1Channel = Channel.CreateUnbounded<IMessageEvent>();
        var child3Channel = Channel.CreateUnbounded<IMessageEvent>();
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parent1] = parent1Channel,
            [parent2] = parent2Channel,
            [child1.Id] = child1Channel,
            [child3.Id] = child3Channel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        aggregator.Rebind(parent1);

        // 先挂载 child1（验证父1 订阅有效）
        await parent1Channel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parent1, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);
        tc1.TaskId.Should().Be(child1.Id);

        // Rebind 到父2 → 父1 订阅应被摘除
        aggregator.Rebind(parent2);

        // 父1 流再发 task 事件（call-3）→ 若父1 订阅未摘除会重新挂载 child3
        await parent1Channel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parent1, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-3", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);

        // child3 事件 → 未被聚合（父1 订阅已摘除，child3 未挂载）
        await child3Channel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = child3.Id, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct3", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
        });
        await Task.Delay(200);

        tc3.TaskId.Should().BeNull();
        tc3.TaskSteps.Should().BeNull();

        parent1Channel.Writer.TryComplete();
        parent2Channel.Writer.TryComplete();
        child1Channel.Writer.TryComplete();
        child3Channel.Writer.TryComplete();
    }

    [Fact]
    public async Task Dispose_ShouldFlushDirtyTaskStepsBeforeReleasingLocks()
    {
        // I3：circuit 关闭（DetachAllForCircuit → ReleaseConsumer → aggregator.Dispose）时，
        // 防抖窗口内未落盘的 TaskSteps 先 flush 到会话存储，再释放锁。
        var parentId = "parent1";
        var childId = "child1";
        var parent = CreateParentWithTaskCall(parentId, "call-1");
        var child = CreateChild(childId, parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var childChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = CreateSessionManagerMock(parent, child);
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [childId] = childChannel
        });

        // 经 Router DI 路径解析聚合器（关联 scope，circuit 关闭时统一释放并触发 flush）
        var scope = new Mock<IServiceScope>();
        var sp = new Mock<IServiceProvider>();
        scope.Setup(s => s.ServiceProvider).Returns(sp.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var router = new SessionEventStreamRouter(
            orchestrator.Object, scopeFactory.Object, NullLogger<SessionEventStreamRouter>.Instance);
        var aggregator = new TaskCardAggregator(router, sm.Object, new TaskSessionResolver(sm.Object));
        sp.Setup(s => s.GetService(typeof(TaskCardAggregator))).Returns(aggregator);

        var agg = router.GetOrCreateConsumer<TaskCardAggregator>(parentId, "circuit-1");
        agg.Should().BeSameAs(aggregator);
        agg.Rebind(parentId);

        // 父流 task 工具调用 → 挂载子流
        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);

        // 子流事件 → 聚合 TaskSteps 并标记 dirty（防抖 1s 窗口内未落盘）
        await childChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = childId, Type = MessageEventType.ToolCallComplete,
            ToolCallId = "ct1", ToolName = "read", Status = ToolCallStatus.Success, Output = "ok"
        });
        await Task.Delay(100);
        parent.Messages[0].ToolCalls[0].TaskSteps.Should().ContainSingle();

        // circuit 关闭 → ReleaseConsumer → aggregator.Dispose → 先 flush 再释放锁
        router.DetachAllForCircuit("circuit-1");

        sm.Verify(m => m.SaveAsync(parentId), Times.AtLeastOnce);

        parentChannel.Writer.TryComplete();
        childChannel.Writer.TryComplete();
        router.Dispose();
    }
}
