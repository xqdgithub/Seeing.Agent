using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class TuiLaunchTests
{
    [Fact]
    public void BuildArguments_WithAllOptions_ShouldEmitPairsInOrder()
    {
        var args = TuiLaunch.BuildArguments(new TuiForwardOptions(
            Continue: true,
            Resume: "ses_1",
            Agent: "plan",
            Model: "gpt-x",
            LogLevel: "Debug",
            Boot: "minimal"));

        args.Should().Equal(
            "--continue=true", "--resume", "ses_1", "--agent", "plan",
            "--model", "gpt-x", "--log-level", "Debug", "--boot", "minimal");
    }

    [Fact]
    public void BuildArguments_ContinueOnly_ShouldUseExplicitValue()
    {
        // 无值布尔开关会被子进程 Host 的命令行配置提供程序消费掉紧随其后的参数。
        TuiLaunch.BuildArguments(new TuiForwardOptions(Continue: true))
            .Should().Equal("--continue=true");
    }

    [Fact]
    public void BuildArguments_WithoutOptions_ShouldBeEmpty()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions()).Should().BeEmpty();
    }

    [Fact]
    public void BuildArguments_WithBlankValues_ShouldSkipThem()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions(Agent: "  ", Model: "")).Should().BeEmpty();
    }

    [Fact]
    public void BuildArguments_ShouldTrimValues()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions(Resume: " ses_1 "))
            .Should().Equal("--resume", "ses_1");
    }

    [Fact]
    public void BuildEnvironment_ShouldPinWorkspaceRootAndContentRoot()
    {
        var env = TuiLaunch.BuildEnvironment(@"D:\work", @"D:\bin");

        env["SEEING_WORKSPACE_ROOT"].Should().Be(@"D:\work");
        env["DOTNET_CONTENTROOT"].Should().Be(@"D:\bin");
    }

    [Fact]
    public void BuildEnvironment_WithoutContentRoot_ShouldOmitIt()
    {
        TuiLaunch.BuildEnvironment(@"D:\work").Should().NotContainKey("DOTNET_CONTENTROOT");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CanRunInteractive_WhenAnyStreamRedirected_ShouldBeFalse(bool input, bool output)
    {
        TuiLaunch.CanRunInteractive(input, output).Should().BeFalse();
    }

    [Fact]
    public void CanRunInteractive_WhenTerminal_ShouldBeTrue()
    {
        TuiLaunch.CanRunInteractive(inputRedirected: false, outputRedirected: false).Should().BeTrue();
    }
}
