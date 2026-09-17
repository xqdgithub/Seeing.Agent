using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class EventStreamHandlerTests
{
    private static EventStreamHandler CreateHandler(string sessionId, SessionData session)
    {
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get(sessionId)).Returns(session);
        return new EventStreamHandler(sessionId, manager.Object);
    }

    [Fact]
    public void SessionId_ShouldMatchBoundId()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        handler.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCallRunning_ShouldWriteToolCallToOwnSession()
    {
        var session = SessionData.Create("p1", "general");
        session.AddMessage(SessionMessage.AssistantMessage("先导内容"));
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new ToolCallEvent
        {
            SessionId = "s1",
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Running
        });

        var msg = session.Messages.Last(m => m.Role == "assistant");
        msg.ToolCalls.Should().ContainSingle(t => t.Id == "t1");
        msg.ToolCalls[0].Status.Should().Be("running");
    }

    [Fact]
    public async Task ProcessEventAsync_TodoUpdate_ShouldSetOwnCurrentTodoList()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new TodoUpdateEvent
        {
            SessionId = "s1",
            Todos = new List<TodoItem>
            {
                new() { Content = "任务A", Status = TodoStatus.Pending, Priority = TodoPriority.Medium }
            }
        });

        handler.CurrentTodoList.Should().NotBeNull();
        handler.CurrentTodoList.Items.Should().ContainSingle(i => i.Content == "任务A");
    }

    [Fact]
    public async Task ProcessEventAsync_BudgetStatus_ShouldSetOwnCurrentBudgetStatus()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new Seeing.Agent.TokenBudget.BudgetStatusEvent
        {
            SessionId = "s1",
            CurrentTokens = 10,
            MaxTokens = 100,
            UsagePercentage = 10,
            Level = BudgetLevel.Normal
        });

        handler.CurrentBudgetStatus.Should().NotBeNull();
        handler.CurrentBudgetStatus.MaxTokens.Should().Be(100);
    }

    [Fact]
    public async Task ProcessEventAsync_ExecutionComplete_MatchingId_ShouldUpdateStatus()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new ExecutionStartedEvent { SessionId = "s1", ExecutionId = "e1" });
        handler.ExecutionStatus.Should().Be(ExecutionStatus.Running);

        await handler.ProcessEventAsync(new ExecutionCompleteEvent { SessionId = "s1", ExecutionId = "e1", Status = ExecutionStatus.Completed });
        handler.ExecutionStatus.Should().Be(ExecutionStatus.Completed);
        handler.IsStreamActive.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessEventAsync_ExecutionComplete_NonMatchingId_ShouldNotClearStatus()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new ExecutionStartedEvent { SessionId = "s1", ExecutionId = "e1" });
        await handler.ProcessEventAsync(new ExecutionCompleteEvent { SessionId = "s1", ExecutionId = "e2", Status = ExecutionStatus.Completed });

        handler.ExecutionStatus.Should().Be(ExecutionStatus.Running);
    }

    [Fact]
    public async Task OnStateChanged_ShouldCarryTriggeringEvent()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);
        IMessageEvent? received = null;
        handler.OnStateChanged += evt => received = evt;

        var evt = new LoopStartEvent { SessionId = "s1", LoopId = "l1" };
        await handler.ProcessEventAsync(evt);

        received.Should().BeSameAs(evt);
    }

    [Fact]
    public void OnEvent_ShouldProcessEvent_WithoutThrowing()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        var act = () => handler.OnEvent(new LoopStartEvent { SessionId = "s1", LoopId = "l1" });

        act.Should().NotThrow();
        handler.GetCurrentLoopId().Should().Be("l1");
    }

    [Fact]
    public void OnStreamEnd_ShouldClearStreamingState()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);

        handler.OnEvent(new LoopStartEvent { SessionId = "s1", LoopId = "l1" });
        handler.GetCurrentLoopId().Should().Be("l1");

        handler.OnStreamEnd();

        handler.GetCurrentLoopId().Should().BeNull();
    }

    [Fact]
    public async Task ProcessEventAsync_BashOutputWithTaskIdLiteral_ShouldNotSetTaskId()
    {
        var session = SessionData.Create("p1", "general");
        session.AddMessage(SessionMessage.AssistantMessage("x"));
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new ToolCallEvent
        {
            SessionId = "s1",
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = "b1",
            ToolName = "bash",
            Status = ToolCallStatus.Success,
            Arguments = new { command = "echo task_id:abc", description = "echo" },
            Output = "task_id:abc\n",
            Title = "echo"
        });

        var tc = session.Messages.Last(m => m.Role == "assistant").ToolCalls!.Single(t => t.Id == "b1");
        tc.Name.Should().Be("bash");
        tc.TaskId.Should().BeNullOrEmpty();
        tc.TaskDescription.Should().BeNullOrEmpty();
        ToolCallViewModel.FromSessionToolCall(tc, "s1").IsTaskTool.Should().BeFalse();
        ToolCallViewModel.FromSessionToolCall(tc, "s1").IsBashTool.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessEventAsync_TaskTool_ShouldStillFillTaskIdFromResult()
    {
        var session = SessionData.Create("p1", "general");
        session.AddMessage(SessionMessage.AssistantMessage("x"));
        var handler = CreateHandler("s1", session);

        await handler.ProcessEventAsync(new ToolCallEvent
        {
            SessionId = "s1",
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Success,
            Arguments = new { description = "探索", subagent_type = "explore" },
            Output = "task_id: child-sid-1\n"
        });

        var tc = session.Messages.Last(m => m.Role == "assistant").ToolCalls!.Single(t => t.Id == "t1");
        tc.TaskId.Should().Be("child-sid-1");
        tc.TaskDescription.Should().Be("探索");
        tc.TaskAgent.Should().Be("explore");
        ToolCallViewModel.FromSessionToolCall(tc, "s1").IsTaskTool.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessEventAsync_PermissionRequest_ShouldRaiseRequestCallback()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);
        PermissionRequestEvent? received = null;
        handler.OnPermissionRequest += evt => received = evt;

        var evt = new PermissionRequestEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            PermissionKind = "tool.execute",
            Resource = "bash",
            CallId = "t1"
        };
        await handler.ProcessEventAsync(evt);

        received.Should().BeSameAs(evt);
    }

    [Fact]
    public async Task ProcessEventAsync_PermissionResolved_ShouldRaiseResolvedCallback()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);
        PermissionResolvedEvent? received = null;
        handler.OnPermissionResolved += evt => received = evt;

        var evt = new PermissionResolvedEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            Decision = PermissionEffect.Allow,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = PermissionResolvedBy.User
        };
        await handler.ProcessEventAsync(evt);

        received.Should().BeSameAs(evt);
    }

    [Fact]
    public void PermissionEventTypes_ShouldUseProtocolStrings()
    {
        MessageEventType.PermissionRequest.Should().Be("permission.request");
        MessageEventType.PermissionResolved.Should().Be("permission.resolved");
    }

    [Fact]
    public async Task ProcessEventAsync_PermissionRequest_ShouldNotMutateSessionMessages()
    {
        var session = SessionData.Create("p1", "general");
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var handler = CreateHandler("s1", session);
        var before = session.Messages.Count;

        await handler.ProcessEventAsync(new PermissionRequestEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            CallId = "t1",
            PermissionKind = "tool.execute",
            Resource = "bash"
        });

        session.Messages.Count.Should().Be(before);
    }

    [Fact]
    public async Task ProcessEventAsync_PermissionResolved_ShouldPreserveDecisionFields()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);
        PermissionResolvedEvent? received = null;
        handler.OnPermissionResolved += evt => received = evt;

        await handler.ProcessEventAsync(new PermissionResolvedEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            CallId = "t1",
            Decision = PermissionEffect.Deny,
            Scope = PermissionGrantScope.Session,
            ResolvedBy = PermissionResolvedBy.Timeout,
            Reason = "超时"
        });

        received.Should().NotBeNull();
        received!.RequestId.Should().Be("r1");
        received.Decision.Should().Be(PermissionEffect.Deny);
        received.Scope.Should().Be(PermissionGrantScope.Session);
        received.ResolvedBy.Should().Be(PermissionResolvedBy.Timeout);
        received.Reason.Should().Be("超时");
    }

    [Fact]
    public async Task Dispose_ShouldDetachPermissionCallbacks()
    {
        var session = SessionData.Create("p1", "general");
        var handler = CreateHandler("s1", session);
        var raised = false;
        handler.OnPermissionResolved += _ => raised = true;

        handler.Dispose();
        await handler.ProcessEventAsync(new PermissionResolvedEvent
        {
            SessionId = "s1",
            RequestId = "r1",
            Decision = PermissionEffect.Deny
        });

        raised.Should().BeFalse();
    }
}
