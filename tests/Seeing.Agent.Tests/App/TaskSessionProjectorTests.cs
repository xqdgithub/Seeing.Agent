using FluentAssertions;
using Seeing.Agent.App.Internal;
using Seeing.Agent.Core.Events;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class TaskSessionProjectorTests
{
    [Fact]
    public void Apply_TaskLifecycle_ShouldPersistTaskFieldsOnToolCall()
    {
        var session = SessionData.Create(selectedAgent: "build");
        session.Messages.Add(SessionMessage.AssistantMessage("thinking"));
        session.Messages[0].Id = "msg1";
        session.Messages[0].LoopId = "loop1";
        session.Messages[0].ToolCalls = new List<SessionToolCall>
        {
            new()
            {
                Id = "call_task_1",
                Name = "task",
                Arguments = """{"description":"查认证","subagent_type":"explore","prompt":"x"}""",
                Status = "pending"
            }
        };

        TaskSessionProjector.Apply(session, new TaskStartedEvent
        {
            SessionId = session.Id,
            LoopId = "loop1",
            TaskId = "child_abc",
            OriginToolCallId = "call_task_1",
            AgentName = "explore",
            Description = "查认证",
            Background = false
        });

        TaskSessionProjector.Apply(session, new TaskCompletedEvent
        {
            SessionId = session.Id,
            LoopId = "loop1",
            TaskId = "child_abc",
            OriginToolCallId = "call_task_1",
            ResultText = "done"
        });

        var tc = session.Messages[0].ToolCalls![0];
        tc.Name.Should().Be("task");
        tc.TaskId.Should().Be("child_abc");
        tc.TaskAgent.Should().Be("explore");
        tc.TaskDescription.Should().Be("查认证");
        tc.Status.Should().Be("success");
        tc.Result.Should().Be("done");
    }

    [Fact]
    public void SessionData_Clone_ShouldDeepCopyTaskFields()
    {
        var session = SessionData.Create();
        session.Messages.Add(new SessionMessage
        {
            Id = "m1",
            Role = "assistant",
            Content = "hi",
            ToolCalls = new List<SessionToolCall>
            {
                new()
                {
                    Id = "c1",
                    Name = "task",
                    TaskId = "child_1",
                    TaskAgent = "explore",
                    TaskDescription = "desc",
                    TaskBackground = true,
                    Status = "success",
                    TaskSteps = new List<SessionTaskStep>
                    {
                        new() { StepKind = "tool", ToolName = "read", Status = "ok" }
                    }
                }
            }
        });

        var clone = session.Clone();
        var tc = clone.Messages[0].ToolCalls![0];
        tc.TaskId.Should().Be("child_1");
        tc.TaskAgent.Should().Be("explore");
        tc.TaskDescription.Should().Be("desc");
        tc.TaskBackground.Should().BeTrue();
        tc.TaskSteps.Should().HaveCount(1);

        // 深拷贝：改 clone 不影响原件
        tc.TaskId = "mutated";
        session.Messages[0].ToolCalls![0].TaskId.Should().Be("child_1");
    }
}
