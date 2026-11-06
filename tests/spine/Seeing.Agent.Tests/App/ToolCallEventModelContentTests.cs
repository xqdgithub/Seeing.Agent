using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ToolCallEventModelContentTests
{
    [Fact]
    public void 失败事件_带定义_应包含定义块()
    {
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "cron_create",
            Status = ToolCallStatus.Failed,
            Error = "缺少必需参数: taskType",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        };

        evt.ModelContent.Should().Contain("<tool_definition>");
        evt.ModelContent.Should().Contain("cron_create");
    }

    [Fact]
    public void 失败事件_无定义_不应出现定义块()
    {
        var evt = new ToolCallEvent
        {
            SessionId = "s1",
            ToolCallId = "c1",
            ToolName = "read",
            Status = ToolCallStatus.Failed,
            Error = "boom"
        };

        evt.ModelContent.Should().NotContain("<tool_definition>");
        evt.ModelContent.Should().Contain("boom");
    }
}
