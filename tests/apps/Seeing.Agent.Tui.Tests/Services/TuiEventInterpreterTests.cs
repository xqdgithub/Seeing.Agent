using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.TokenBudget;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiEventInterpreterTests
{
    private const string Session = "ses_test";

    private static TuiViewState NewState() => new() { SessionId = Session };

    [Fact]
    public void StreamStart_ThenDelta_ShouldAccumulateTextAndReasoning()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new StreamStartEvent { SessionId = Session, LoopId = "loop1", Step = 0 }).Should().BeTrue();
        sut.Apply(new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ContentDelta = "Hel" }).Should().BeTrue();
        sut.Apply(new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ContentDelta = "lo" }).Should().BeTrue();
        sut.Apply(new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ReasoningDelta = "think" }).Should().BeTrue();

        var block = state.Find(TuiViewState.AssistantKey("loop1", 0, null));
        block.Should().NotBeNull();
        block!.Text.Should().Be("Hello");
        block.Reasoning.Should().Be("think");
        block.IsStreaming.Should().BeTrue();
        block.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void StreamDelta_WithoutSeenStreamStart_ShouldBeDropped()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ContentDelta = "orphan" }).Should().BeFalse();

        state.Blocks.Should().BeEmpty();
        state.Find(TuiViewState.AssistantKey("loop1", 0, null)).Should().BeNull();
    }

    [Fact]
    public void StreamComplete_ShouldOverwriteWholeStep()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new StreamStartEvent { SessionId = Session, LoopId = "loop1", Step = 0 });
        sut.Apply(new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ContentDelta = "partial" });
        sut.Apply(new StreamCompleteEvent
        {
            SessionId = Session,
            LoopId = "loop1",
            Message = new ChatMessage { Role = "assistant", Content = "final", ReasoningContent = "reasoned" },
        }).Should().BeTrue();

        var block = state.Find(TuiViewState.AssistantKey("loop1", 0, null));
        block!.Text.Should().Be("final");
        block.Reasoning.Should().Be("reasoned");
        block.IsStreaming.Should().BeFalse();
        block.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void StreamComplete_WithoutStreamStart_ShouldStillOverwrite()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new StreamCompleteEvent
        {
            SessionId = Session,
            LoopId = "loop1",
            Message = new ChatMessage { Role = "assistant", Content = "late" },
        }).Should().BeTrue();

        state.Find(TuiViewState.AssistantKey("loop1", 0, null))!.Text.Should().Be("late");
    }

    [Fact]
    public void ExecutionStarted_ShouldSetExecutionState_ButLoopStartShouldNot()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new LoopStartEvent { SessionId = Session, LoopId = "loop1" });
        sut.IsExecuting.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();

        sut.Apply(new ExecutionStartedEvent { SessionId = Session, ExecutionId = "exec1" }).Should().BeTrue();
        sut.IsExecuting.Should().BeTrue();
        state.ActiveExecutionId.Should().Be("exec1");
    }

    [Fact]
    public void ExecutionComplete_ShouldClearOnlyWhenExecutionIdMatches()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new ExecutionStartedEvent { SessionId = Session, ExecutionId = "exec1" });

        sut.Apply(new ExecutionCompleteEvent { SessionId = Session, ExecutionId = "other" }).Should().BeFalse();
        sut.IsExecuting.Should().BeTrue();

        sut.Apply(new ExecutionCompleteEvent { SessionId = Session, ExecutionId = "exec1" }).Should().BeTrue();
        sut.IsExecuting.Should().BeFalse();
        state.ActiveExecutionId.Should().BeNull();
    }

    [Fact]
    public void LoopComplete_ShouldNotClearExecutionState()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new ExecutionStartedEvent { SessionId = Session, ExecutionId = "exec1" });
        sut.Apply(new LoopCompleteEvent { SessionId = Session, LoopId = "loop1" });

        sut.IsExecuting.Should().BeTrue();
    }

    [Fact]
    public void ToolCall_ShouldUpsertByCallId_AndMapStatus()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new ToolCallEvent
        {
            SessionId = Session,
            ToolCallId = "call1",
            ToolName = "bash",
            Status = ToolCallStatus.Running,
            Arguments = "{\"cmd\":\"ls\"}",
        });
        sut.Apply(new ToolCallEvent
        {
            SessionId = Session,
            ToolCallId = "call1",
            ToolName = "bash",
            Status = ToolCallStatus.Success,
            Output = "ok",
            Title = "list",
        });

        state.Tools.Should().HaveCount(1);
        var tool = state.Find("tool:call1")!.Tool!;
        tool.Status.Should().Be(TuiToolStatus.Success);
        tool.Arguments.Should().Be("{\"cmd\":\"ls\"}");
        tool.Output.Should().Be("ok");
        tool.Title.Should().Be("list");
        state.Find("tool:call1")!.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void ToolCall_Task_ShouldResolveTaskIdFromMetadataOrOutputPrefix()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new ToolCallEvent
        {
            SessionId = Session,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Running,
            Metadata = new Dictionary<string, object> { ["task_id"] = "child1" },
        });
        state.Find("tool:t1")!.Tool!.TaskId.Should().Be("child1");

        sut.Apply(new ToolCallEvent
        {
            SessionId = Session,
            ToolCallId = "t2",
            ToolName = "task",
            Status = ToolCallStatus.Success,
            Output = "header line\ntask_id: child2\ntrailer",
        });
        state.Find("tool:t2")!.Tool!.TaskId.Should().Be("child2");
    }

    [Fact]
    public void TodoUpdate_ShouldReplaceWholeList()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new TodoUpdateEvent
        {
            SessionId = Session,
            Todos = [new TodoItem { Content = "a", Status = TodoStatus.Pending }],
        });
        state.Todos.Should().HaveCount(1);
        state.Todos[0].Status.Should().Be("pending");

        sut.Apply(new TodoUpdateEvent
        {
            SessionId = Session,
            Todos = [new TodoItem { Content = "b", Status = TodoStatus.InProgress }],
        });
        state.Todos.Should().HaveCount(1);
        state.Todos[0].Content.Should().Be("b");
        state.Todos[0].Status.Should().Be("in_progress");
    }

    [Fact]
    public void BudgetStatus_ShouldUpdateBudget()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new BudgetStatusEvent
        {
            SessionId = Session,
            CurrentTokens = 120,
            MaxTokens = 400,
            UsagePercentage = 30,
        }).Should().BeTrue();

        state.Budget.Should().NotBeNull();
        state.Budget!.InputTokens.Should().Be(120);
        state.Budget.Limit.Should().Be(400);
    }

    [Fact]
    public void SessionTitleChanged_And_ModeUpdate_ShouldUpdateState()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new Seeing.Agent.Core.Events.SessionTitleChangedEvent { SessionId = Session, Title = "T" }).Should().BeTrue();
        state.Title.Should().Be("T");

        sut.Apply(new ModeUpdateEvent { SessionId = Session, ModeId = "ask" }).Should().BeTrue();
        state.AcpMode.Should().Be("ask");
    }

    [Fact]
    public void UnknownEvent_ShouldReturnFalse_WithoutThrowing()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);

        sut.Apply(new StubEvent(Session, "loop1", DateTime.Now)).Should().BeFalse();
        sut.Apply(new NavigateEvent { SessionId = Session, Target = "/x" }).Should().BeFalse();
    }

    [Fact]
    public void FullReplay_ShouldNotDuplicateBlocks_AndRevisionMonotonic()
    {
        var state = NewState();
        var sut = new TuiEventInterpreter(state);
        var events = BuildLoopReplay();

        foreach (var evt in events)
            sut.Apply(evt);

        var blocks = state.Blocks.Count;
        var revision = state.Revision;
        var text = state.Find(TuiViewState.AssistantKey("loop1", 0, null))!.Text;

        foreach (var evt in events)
            sut.Apply(evt);

        state.Blocks.Count.Should().Be(blocks);
        state.Revision.Should().BeGreaterThanOrEqualTo(revision);
        state.Find(TuiViewState.AssistantKey("loop1", 0, null))!.Text.Should().Be(text);
        state.Tools.Should().HaveCount(1);
    }

    private static IEnumerable<IMessageEvent> BuildLoopReplay()
    {
        yield return new LoopStartEvent { SessionId = Session, LoopId = "loop1" };
        yield return new ExecutionStartedEvent { SessionId = Session, ExecutionId = "exec1" };
        yield return new StreamStartEvent { SessionId = Session, LoopId = "loop1", Step = 0 };
        yield return new StreamDeltaEvent { SessionId = Session, LoopId = "loop1", ContentDelta = "final" };
        yield return new StreamCompleteEvent
        {
            SessionId = Session,
            LoopId = "loop1",
            Message = new ChatMessage { Role = "assistant", Content = "final" },
        };
        yield return new ToolCallEvent
        {
            SessionId = Session,
            ToolCallId = "call1",
            ToolName = "bash",
            Status = ToolCallStatus.Success,
            Output = "ok",
        };
        yield return new ExecutionCompleteEvent { SessionId = Session, ExecutionId = "exec1" };
        yield return new LoopCompleteEvent { SessionId = Session, LoopId = "loop1" };
    }

    private sealed record StubEvent(string SessionId, string? LoopId, DateTime Timestamp) : IMessageEvent
    {
        public string Type => "unknown.event";
    }
}
