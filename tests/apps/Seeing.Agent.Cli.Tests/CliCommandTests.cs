using System.Diagnostics;
using System.Linq;

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
    public void CreateTuiCommand_ShouldExposeTuiEntry()
    {
        var command = TuiCommand.Create();

        command.Name.Should().Be("tui");
        command.Options
            .Select(o => o.Name.TrimStart('-'))
            .Should().Contain(new[] { "continue", "resume", "agent", "model", "log-level", "boot" });
    }

    [Fact]
    public void Create_ShouldListTuiAsSupportedService()
    {
        StartCommand.SupportedServices.Should().Contain("tui");
    }

    [Fact]
    public void CreateWeb_ShouldExposeRunModeSwitches()
    {
        var names = StartCommand.CreateWeb().Options
            .Select(o => o.Name.TrimStart('-'))
            .ToList();

        names.Should().Contain("background");
        names.Should().Contain("foreground");
        names.Should().Contain("boot");
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
