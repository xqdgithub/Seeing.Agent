using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tui.Tests;

/// <summary>
/// <c>TuiCliOptions.MouseEnabled</c> 解析回归（spec §8）：默认开、<c>--no-mouse</c> 关、与其它选项共存。
/// </summary>
public sealed class TuiCliOptionsMouseTests
{
    [Fact]
    public void Parse_WithoutArguments_ShouldEnableMouseByDefault()
    {
        TuiCliOptions.Parse(Array.Empty<string>()).MouseEnabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoMouseSwitch_ShouldDisableMouse()
    {
        TuiCliOptions.Parse(["--no-mouse"]).MouseEnabled.Should().BeFalse();
    }

    [Fact]
    public void Parse_NoMouseWithOtherOptions_ShouldCoexist()
    {
        var options = TuiCliOptions.Parse(
        [
            "--no-mouse",
            "--continue=true",
            "--agent", "plan",
            "--model", "gpt-x",
            "--log-level", "Debug",
            "--boot", "minimal",
        ]);

        options.MouseEnabled.Should().BeFalse();
        options.Continue.Should().BeTrue();
        options.Agent.Should().Be("plan");
        options.Model.Should().Be("gpt-x");
        options.LogLevel.Should().Be("Debug");
    }

    [Fact]
    public void Parse_InvalidArguments_ShouldFallBackToMouseEnabled()
    {
        // 解析失败回退全默认（含鼠标默认开），与其余选项的回退行为一致。
        var options = TuiCliOptions.Parse(["--nope"], new StringWriter());

        options.MouseEnabled.Should().BeTrue();
    }

    [Fact]
    public void DefaultOptions_MouseEnabledShouldBeTrue()
    {
        new TuiCliOptions().MouseEnabled.Should().BeTrue();
    }
}
