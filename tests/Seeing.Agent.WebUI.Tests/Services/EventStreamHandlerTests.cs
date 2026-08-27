using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Execution;
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
}
