using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Tools.BuiltIn.Time;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class CurrentTimeToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutTimezone_ShouldReturnIso8601LocalTime()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { });

        var before = DateTimeOffset.Now.AddSeconds(-2);
        var result = await tool.ExecuteAsync(args, new ToolContext());
        var after = DateTimeOffset.Now.AddSeconds(2);

        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNullOrWhiteSpace();
        var parsed = DateTimeOffset.Parse(result.Output!);
        parsed.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        parsed.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(parsed));
    }

    [Fact]
    public async Task ExecuteAsync_WithAsiaShanghai_ShouldReturnOffsetPlusEight()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { timezone = "Asia/Shanghai" });

        var result = await tool.ExecuteAsync(args, new ToolContext());

        result.Success.Should().BeTrue();
        var parsed = DateTimeOffset.Parse(result.Output!);
        parsed.Offset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidTimezone_ShouldFail()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { timezone = "Not/A_Real_Zone" });

        var result = await tool.ExecuteAsync(args, new ToolContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Not/A_Real_Zone");
        result.Error.Should().Contain("Asia/Shanghai");
    }
}
