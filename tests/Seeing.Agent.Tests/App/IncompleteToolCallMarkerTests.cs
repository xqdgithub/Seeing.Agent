using FluentAssertions;
using Seeing.Agent.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class IncompleteToolCallMarkerTests
{
    [Fact]
    public void MarkCancelled_RunningNonTaskToolCall_ShouldMarkCancelled()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_read", Name = "read", Status = "running" }
            }
        });

        var count = IncompleteToolCallMarker.MarkCancelled(session, "用户取消");

        count.Should().Be(1);
        session.Messages[0].ToolCalls![0].Status.Should().Be("cancelled");
        session.Messages[0].ToolCalls![0].Error.Should().Be("用户取消");
    }

    [Fact]
    public void MarkCancelled_PendingTaskToolCall_ShouldMarkCancelled()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_task", Name = "task", TaskId = "child_1", Status = "pending" }
            }
        });

        var count = IncompleteToolCallMarker.MarkCancelled(session, "超时");

        count.Should().Be(1);
        session.Messages[0].ToolCalls![0].Status.Should().Be("cancelled");
        session.Messages[0].ToolCalls![0].Error.Should().Be("超时");
    }

    [Fact]
    public void MarkCancelled_TerminalToolCalls_ShouldLeaveUntouched()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_ok", Name = "read", Status = "success", Result = "ok" },
                new() { Id = "call_fail", Name = "glob", Status = "failed", Error = "boom" },
                new() { Id = "call_rej", Name = "write", Status = "rejected", Error = "denied" }
            }
        });

        var count = IncompleteToolCallMarker.MarkCancelled(session, "用户取消");

        count.Should().Be(0);
        session.Messages[0].ToolCalls![0].Status.Should().Be("success");
        session.Messages[0].ToolCalls![1].Status.Should().Be("failed");
        session.Messages[0].ToolCalls![2].Status.Should().Be("rejected");
    }

    [Fact]
    public void MarkCancelled_MixedStatuses_ShouldOnlyMarkIncomplete()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_running", Name = "read", Status = "running" },
                new() { Id = "call_done", Name = "read", Status = "success", Result = "ok" }
            }
        });

        var count = IncompleteToolCallMarker.MarkCancelled(session, "取消");

        count.Should().Be(1);
        session.Messages[0].ToolCalls![0].Status.Should().Be("cancelled");
        session.Messages[0].ToolCalls![1].Status.Should().Be("success");
    }
}
