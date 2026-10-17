using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Shell;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class DefaultShellServiceSubprocessTests
{
    [Fact]
    public void SelectShell_FindExecutable_ShouldUseISubprocess()
    {
        SubprocessSpec? captured = null;
        var factory = new Mock<ISubprocessFactory>();
        factory.Setup(f => f.Start(It.IsAny<SubprocessSpec>())).Returns((SubprocessSpec spec) =>
        {
            captured = spec;
            return new FakeSubprocess("C:\\Windows\\System32\\cmd.exe\n");
        });

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Subprocess).Returns(factory.Object);

        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions
        {
            PreferredShells = ["cmd"]
        });

        // Force path that calls FindExecutable: clear SHELL and use PreferredShells
        var prev = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            Environment.SetEnvironmentVariable("SHELL", null);
            // Prefer a name that isn't COMSPEC shortcut alone — use "pwsh" so FindExecutable runs
            options.Setup(o => o.CurrentValue).Returns(new ShellOptions
            {
                PreferredShells = ["pwsh"]
            });

            var svc = new DefaultShellService(
                NullLogger<DefaultShellService>.Instance,
                options.Object,
                world.Object);

            var shell = svc.SelectShell();
            captured.Should().NotBeNull();
            captured!.FileName.Should().BeOneOf("where", "which");
            captured.Arguments.Should().Be("pwsh");
            shell.Should().Be(@"C:\Windows\System32\cmd.exe");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", prev);
        }
    }

    [Fact]
    public void BuildArgumentList_PosixShell_ShouldPassCommandAsOneArg()
    {
        var svc = CreateService();
        const string command = "echo step15";

        svc.BuildArgumentList("/bin/bash", command).Should().Equal("-c", command);
        svc.BuildArgumentList("/bin/sh", "printf '%s' \"a b'c\"").Should().Equal("-c", "printf '%s' \"a b'c\"");
    }

    [Fact]
    public void BuildArgumentList_WindowsShells_ShouldStayOnRawCommandLine()
    {
        var svc = CreateService();

        svc.BuildArgumentList("cmd.exe", "echo hi").Should().BeNull();
        svc.BuildArgumentList("pwsh", "echo hi").Should().BeNull();
        svc.BuildArguments("cmd.exe", "echo \"hi\"").Should().Be("/c \"echo \\\"hi\\\"\"");
    }

    private static DefaultShellService CreateService()
    {
        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Subprocess).Returns(Mock.Of<ISubprocessFactory>());
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());
        return new DefaultShellService(NullLogger<DefaultShellService>.Instance, options.Object, world.Object);
    }

    private sealed class FakeSubprocess : ISubprocess
    {
        public FakeSubprocess(string stdout)
        {
            StandardOutput = new StringReader(stdout);
            StandardError = new StringReader("");
        }

        public int Id => 1;
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public bool HasExited => true;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Kill(bool entireTree = true) { }
        public void Dispose() { }
    }
}
