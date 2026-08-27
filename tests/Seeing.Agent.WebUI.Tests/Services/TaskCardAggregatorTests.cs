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
        var aggregator = new TaskCardAggregator(router, sm.Object);
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
        var aggregator = new TaskCardAggregator(router, sm.Object);
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
}
