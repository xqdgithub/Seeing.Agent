using System.Diagnostics;

using FluentAssertions;
using Seeing.Agent.Cli.Commands;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class CliCommandTests
{
    [Fact]
    public void CreateCommands_ShouldExposeStartAndShortcutCommands()
    {
        StartCommand.Create().Name.Should().Be("start");
        StartCommand.CreateWeb().Name.Should().Be("web");
        StartCommand.CreateGateway().Name.Should().Be("gateway");
    }

    [Fact]
    public void BrowserLauncher_WhenBrowserProcessStarts_ShouldReturnSuccess()
    {
        ProcessStartInfo? captured = null;
        var opened = BrowserLauncher.TryOpen(
            "http://127.0.0.1:25123",
            startInfo =>
            {
                captured = startInfo;
                return true;
            },
            out var error);

        opened.Should().BeTrue();
        error.Should().BeNull();
        captured.Should().NotBeNull();
        if (OperatingSystem.IsWindows())
            captured!.FileName.Should().Be("http://127.0.0.1:25123");
        else
            captured!.ArgumentList.Should().Contain("http://127.0.0.1:25123");
    }
}
