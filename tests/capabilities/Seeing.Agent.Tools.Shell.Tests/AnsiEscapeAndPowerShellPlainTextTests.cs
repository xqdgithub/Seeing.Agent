using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Shell;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class AnsiEscapeAndPowerShellPlainTextTests
{
    [Theory]
    [InlineData("\u001B[32;1mMode\u001B[0m", "Mode")]
    [InlineData("\u001B[44;1m.idea\u001B[0m", ".idea")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Strip_ShouldRemoveAnsiColorSequences(string input, string expected)
    {
        AnsiEscape.Strip(input).Should().Be(expected);
    }

    [Fact]
    public void PrepareCommand_PowerShell_ShouldForcePlainTextRendering()
    {
        var svc = CreateShellService();
        var prepared = svc.PrepareCommand("pwsh", "Get-ChildItem");
        prepared.Should().Contain("$PSStyle.OutputRendering = 'PlainText'");
        prepared.Should().Contain("Get-ChildItem");
    }

    private static IShellService CreateShellService()
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());
        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Subprocess).Returns(Mock.Of<ISubprocessFactory>());
        return new DefaultShellService(NullLogger<DefaultShellService>.Instance, options.Object, world.Object);
    }
}
