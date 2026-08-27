using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionJobServiceBuildHistoryTests
{
    [Fact]
    public void BuildHistoryFromSession_AssistantToolCallsWithResults_ShouldEmitFollowingToolMessages()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_1", Name = "read", Result = "文件内容A", Status = "success" },
                new() { Id = "call_2", Name = "glob", Result = "匹配结果B", Status = "success" }
            }
        });
        session.AddMessage(SessionMessage.UserMessage("继续"));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(4);
        history[0].Role.Should().Be(ChatRole.Assistant);
        history[0].ToolCalls.Should().HaveCount(2);

        history[1].Role.Should().Be(ChatRole.Tool);
        history[1].ToolCallId.Should().Be("call_1");
        history[1].Content.Should().Be("文件内容A");

        history[2].Role.Should().Be(ChatRole.Tool);
        history[2].ToolCallId.Should().Be("call_2");
        history[2].Content.Should().Be("匹配结果B");

        history[3].Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public void BuildHistoryFromSession_ExistingToolMessage_ShouldPreserveToolCallId()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.ToolMessage("结果", "call_x", "read"));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Role.Should().Be(ChatRole.Tool);
        history[0].ToolCallId.Should().Be("call_x");
        history[0].Content.Should().Be("结果");
    }

    [Fact]
    public void BuildHistoryFromSession_ToolCallWithError_ShouldEmitToolMessageWithError()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_e", Name = "read", Error = "执行失败", Status = "failed" }
            }
        });

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(2);
        history[1].Role.Should().Be(ChatRole.Tool);
        history[1].ToolCallId.Should().Be("call_e");
        history[1].Content.Should().Be("执行失败");
    }

    [Fact]
    public void BuildHistoryFromSession_ToolCallWithoutResult_ShouldEmitEmptyToolMessage()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = new List<SessionToolCall>
            {
                new() { Id = "call_r", Name = "read", Status = "rejected" }
            }
        });

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(2);
        history[1].Role.Should().Be(ChatRole.Tool);
        history[1].ToolCallId.Should().Be("call_r");
    }
}
