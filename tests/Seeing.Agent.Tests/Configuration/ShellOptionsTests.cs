using FluentAssertions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ShellOptionsTests
{
    [Fact]
    public void Defaults_PreferredShells_ShouldBePwshPowershellBashCmd()
    {
        var options = new ShellOptions();
        options.PreferredShells.Should().Equal("pwsh", "powershell", "bash", "cmd");
    }

    [Fact]
    public void Defaults_BlockedCommands_ShouldNotContainRmOrDel()
    {
        var options = new ShellOptions();
        options.BlockedCommands.Should().NotContain("rm");
        options.BlockedCommands.Should().NotContain("rmdir");
        options.BlockedCommands.Should().NotContain("del");
        options.BlockedCommands.Should().NotContain("systemctl");
        options.BlockedCommands.Should().Contain("dd");
    }

    [Fact]
    public void Defaults_BlockedPatterns_ShouldNotContainRmDeletePatterns()
    {
        var options = new ShellOptions();
        options.BlockedPatterns.Should().NotContain("rm -rf /");
        options.BlockedPatterns.Should().NotContain("rm -r /");
        options.BlockedPatterns.Should().NotContain("del /f /s C:\\");
        options.BlockedPatterns.Should().Contain("> /etc/");
    }

    [Fact]
    public void Defaults_EnableCommandGuard_ShouldBeFalse()
    {
        new ShellOptions().EnableCommandGuard.Should().BeFalse();
    }
}
