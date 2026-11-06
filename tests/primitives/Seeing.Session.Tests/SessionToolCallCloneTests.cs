using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests;

public class SessionToolCallCloneTests
{
    [Fact]
    public void 克隆_应拷贝工具定义()
    {
        var original = new SessionToolCall
        {
            Id = "c1",
            Name = "cron_create",
            Status = "failed",
            ToolDefinition = "{\"name\":\"cron_create\"}"
        };

        original.Clone().ToolDefinition.Should().Be("{\"name\":\"cron_create\"}");
    }
}
