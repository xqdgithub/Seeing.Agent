using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using System.Collections.Immutable;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ToolDefinitionPersistenceTests
{
    [Fact]
    public void 投影_失败工具调用带定义_应写入会话工具调用()
    {
        var session = SessionData.Create();
        var tracker = new ChatEventTracker();

        tracker.ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        });

        var call = session.GetActiveMessages()
            .SelectMany(m => m.ToolCalls ?? ImmutableList<SessionToolCall>.Empty)
            .Single(t => t.Id == "c1");

        call.ToolDefinition.Should().Be("{\"name\":\"cron_create\"}");
    }

    [Fact]
    public void 历史重建_应输出定义块()
    {
        var session = SessionData.Create();
        session.AddMessage(new SessionMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ToolCalls = ImmutableList.Create(new SessionToolCall
            {
                Id = "c1",
                Name = "cron_create",
                Status = "failed",
                Error = "缺少必需参数: taskType",
                ToolDefinition = "{\"name\":\"cron_create\"}"
            })
        });

        var history = ExecutionRunner.BuildHistoryFromSession(session);
        var tool = history.Single(m => m.Role == ChatRole.Tool);

        tool.Content.Should().Contain("<tool_definition>");
        tool.Content.Should().Contain("cron_create");
    }

    [Fact]
    public void 本轮内容与历史重建_应逐字节相同()
    {
        const string def = "{\"name\":\"cron_create\",\"parameters\":{\"type\":\"object\"}}";
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = def
        };

        var session = SessionData.Create();
        new ChatEventTracker().ApplyEvent(session, evt);

        var rebuilt = ExecutionRunner.BuildHistoryFromSession(session)
            .Single(m => m.Role == ChatRole.Tool);

        rebuilt.Content.Should().Be(evt.ModelContent);
    }

    [Fact]
    public void 克隆往返后_定义不应丢失()
    {
        // 落盘走 SessionMessage.Clone()，逐字段枚举；漏拷字段会导致「内存对、读回丢」。
        const string def = "{\"name\":\"cron_create\"}";
        var session = SessionData.Create();
        new ChatEventTracker().ApplyEvent(session, new ToolCallEvent
        {
            SessionId = session.Id,
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "boom",
            ToolDefinition = def
        });

        var message = session.GetActiveMessages().Single();
        var cloned = message.Clone();
        var call = cloned.ToolCalls!.Single();

        call.ToolDefinition.Should().Be(def);
    }
}
