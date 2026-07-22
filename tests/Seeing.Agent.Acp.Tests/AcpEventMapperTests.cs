using Acp.Types;
using FluentAssertions;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Tools.BuiltIn.Todo;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpEventMapperTests
{
    private readonly AcpEventMapper _mapper = new();

    [Fact]
    public void Map_AgentMessageChunk_ShouldEmitStreamDelta()
    {
        var update = new AgentMessageChunk
        {
            Content = new TextContentBlock("hello")
        };

        var events = _mapper.Map(update, "sess-1", "loop-1").ToList();

        events.Should().ContainSingle();
        events[0].Should().BeOfType<StreamDeltaEvent>()
            .Which.ContentDelta.Should().Be("hello");
    }

    [Fact]
    public void Map_ToolCallStart_ShouldEmitPendingToolCall()
    {
        var update = new ToolCallStart
        {
            ToolCallId = "tc-1",
            ToolName = "read",
            Title = "Read file"
        };

        var evt = _mapper.Map(update, "sess-1", "loop-1").Single().Should().BeOfType<ToolCallEvent>().Subject;

        evt.Type.Should().Be(MessageEventType.ToolCallPending);
        evt.ToolName.Should().Be("read");
        evt.Status.Should().Be(ToolCallStatus.Pending);
    }

    [Fact]
    public void Map_ToolCallProgressCompleted_ShouldEmitComplete()
    {
        var update = new ToolCallProgress
        {
            ToolCallId = "tc-1",
            ToolName = "bash",
            Status = "completed"
        };

        var evt = _mapper.Map(update, "sess-1", null).Single().Should().BeOfType<ToolCallEvent>().Subject;
        evt.Type.Should().Be(MessageEventType.ToolCallComplete);
        evt.Status.Should().Be(ToolCallStatus.Success);
    }

    [Fact]
    public void SyntheticEvents_ShouldCreateLoopLifecycle()
    {
        var loopStart = _mapper.CreateLoopStart("sess", "loop", "hi");
        var streamStart = _mapper.CreateStreamStart("sess", "loop");
        var loopComplete = _mapper.CreateLoopComplete("sess", "loop", true, TimeSpan.FromSeconds(1));

        loopStart.Type.Should().Be(MessageEventType.LoopStart);
        streamStart.Type.Should().Be(MessageEventType.StreamStart);
        loopComplete.Type.Should().Be(MessageEventType.LoopComplete);
        loopComplete.Success.Should().BeTrue();
    }

    [Fact]
    public void Map_UnknownSessionUpdate_ShouldNotThrow()
    {
        var update = new UnknownSessionUpdate { SessionUpdateKind = "custom_event" };
        var events = _mapper.Map(update, "sess", "loop").ToList();
        events.Should().BeEmpty();
    }

    [Fact]
    public void Map_AvailableCommandsUpdate_ShouldNotEmitAssistantText()
    {
        var update = new AvailableCommandsUpdate
        {
            AvailableCommands =
            [
                new AvailableCommand { Name = "agent-browser" },
                new AvailableCommand { Name = "brainstorming" }
            ]
        };

        _mapper.Map(update, "sess", "loop").Should().BeEmpty();
    }

    [Fact]
    public void Map_AgentPlanUpdate_ShouldEmitTodoUpdateEvent()
    {
        var update = new AgentPlanUpdate
        {
            Entries =
            [
                new PlanEntry { Content = "Task 1", Priority = "high", Status = "pending" },
                new PlanEntry { Content = "Task 2", Priority = "medium", Status = "in_progress" },
                new PlanEntry { Content = "Task 3", Priority = "low", Status = "completed" }
            ]
        };

        var evt = _mapper.Map(update, "sess-1", "loop-1").Single().Should().BeOfType<TodoUpdateEvent>().Subject;

        evt.Type.Should().Be(MessageEventType.TodoUpdate);
        evt.SessionId.Should().Be("sess-1");
        evt.LoopId.Should().Be("loop-1");
        evt.Todos.Should().HaveCount(3);

        evt.Todos[0].Content.Should().Be("Task 1");
        evt.Todos[0].Priority.Should().Be(TodoPriority.High);
        evt.Todos[0].Status.Should().Be(TodoStatus.Pending);

        evt.Todos[1].Content.Should().Be("Task 2");
        evt.Todos[1].Priority.Should().Be(TodoPriority.Medium);
        evt.Todos[1].Status.Should().Be(TodoStatus.InProgress);

        evt.Todos[2].Content.Should().Be("Task 3");
        evt.Todos[2].Priority.Should().Be(TodoPriority.Low);
        evt.Todos[2].Status.Should().Be(TodoStatus.Completed);
    }

    [Fact]
    public void Map_AgentPlanUpdate_EmptyEntries_ShouldEmitEmptyTodoList()
    {
        var update = new AgentPlanUpdate
        {
            Entries = []
        };

        var evt = _mapper.Map(update, "sess-1", null).Single().Should().BeOfType<TodoUpdateEvent>().Subject;

        evt.Todos.Should().BeEmpty();
    }

    [Fact]
    public void Map_CurrentModeUpdate_ShouldEmitModeUpdateEvent()
    {
        var update = new CurrentModeUpdate
        {
            CurrentModeId = "build"
        };

        var evt = _mapper.Map(update, "sess-1", "loop-1").Single().Should().BeOfType<ModeUpdateEvent>().Subject;

        evt.Type.Should().Be(MessageEventType.ModeUpdate);
        evt.SessionId.Should().Be("sess-1");
        evt.LoopId.Should().Be("loop-1");
        evt.ModeId.Should().Be("build");
    }
}
