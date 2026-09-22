using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tui.Tests;

/// <summary>
/// 锁定 CLI（seeing-cli）转发给 seeing-tui 的参数线格式：
/// seeing-cli 的 TuiLaunch.BuildArguments 生成的串必须能被此处解析出预期选项。
/// </summary>
public class TuiCliOptionsParserTests
{
    [Fact]
    public void Parse_ForwardedContinueSwitch_ShouldSetContinue()
    {
        // seeing-cli 发送 `--continue=true`（显式赋值），避免 Host 的命令行配置提供程序吞掉后续参数。
        var options = TuiCliOptions.Parse(new[] { "--continue=true" });

        options.Continue.Should().BeTrue();
    }

    [Fact]
    public void Parse_FullForwardedArgumentSet_ShouldMapEveryOption()
    {
        var options = TuiCliOptions.Parse(new[]
        {
            "--continue=true",
            "--resume", "ses_1",
            "--agent", "plan",
            "--model", "gpt-x",
            "--log-level", "Debug",
            "--boot", "minimal",
        });

        options.Continue.Should().BeTrue();
        options.Resume.Should().Be("ses_1");
        options.Agent.Should().Be("plan");
        options.Model.Should().Be("gpt-x");
        options.LogLevel.Should().Be("Debug");
    }

    [Fact]
    public void Parse_WithoutArguments_ShouldUseDefaults()
    {
        var options = TuiCliOptions.Parse(Array.Empty<string>());

        options.Continue.Should().BeFalse();
        options.Resume.Should().BeNull();
        options.Agent.Should().BeNull();
        options.Model.Should().BeNull();
    }
}
