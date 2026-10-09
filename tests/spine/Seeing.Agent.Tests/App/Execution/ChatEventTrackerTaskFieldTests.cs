using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class ChatEventTrackerTaskFieldTests
{
    private static SessionToolCall FirstToolCall(SessionData session) =>
        session.Messages.Last(m => m.Role == MessageRole.Assistant).ToolCalls!.Single();

    [Fact]
    public void ToolCallComplete_WithTaskMetadata_MapsTaskFields()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();
        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Success,
            Metadata = new Dictionary<string, object>
            {
                [TaskMetadataKeys.TaskId] = "child-1",
                [TaskMetadataKeys.TaskAgent] = "explore",
                [TaskMetadataKeys.TaskDescription] = "探索代码",
                [TaskMetadataKeys.TaskBackground] = true
            }
        });

        var tc = FirstToolCall(session);
        tc.TaskId.Should().Be("child-1");
        tc.TaskAgent.Should().Be("explore");
        tc.TaskDescription.Should().Be("探索代码");
        tc.TaskBackground.Should().BeTrue();
    }

    [Fact]
    public void ToolCallRunning_TaskWithoutMetadata_FillsFromArguments()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();
        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = "t1",
            ToolName = "task",
            Status = ToolCallStatus.Running,
            Arguments = new { description = "探索", subagent_type = "explore", background = true }
        });

        var tc = FirstToolCall(session);
        tc.TaskDescription.Should().Be("探索");
        tc.TaskAgent.Should().Be("explore");
        tc.TaskBackground.Should().BeTrue();
        tc.TaskId.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ToolCallNonTask_DoesNotSetTaskFields()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();
        tracker.ApplyEvent(session, new LoopStartEvent { SessionId = session.Id, LoopId = "L1" });
        tracker.ApplyEvent(session, new StreamStartEvent { SessionId = session.Id, LoopId = "L1", Step = 0 });

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            LoopId = "L1",
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = "b1",
            ToolName = "bash",
            Status = ToolCallStatus.Success,
            Arguments = new { command = "echo task_id:abc" },
            Output = "task_id:abc\n"
        });

        var tc = FirstToolCall(session);
        tc.TaskId.Should().BeNullOrEmpty();
        tc.TaskDescription.Should().BeNullOrEmpty();
    }
}
