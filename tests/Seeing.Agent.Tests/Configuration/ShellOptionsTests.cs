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
    public void Defaults_BlockedPatterns_ShouldContainRmRfRoot()
    {
        var options = new ShellOptions();
        options.BlockedPatterns.Should().Contain("rm -rf /");
        options.BlockedPatterns.Should().Contain("format C:");
    }

    [Fact]
    public void Defaults_EnableCommandGuard_ShouldBeTrue()
    {
        new ShellOptions().EnableCommandGuard.Should().BeTrue();
    }
}
