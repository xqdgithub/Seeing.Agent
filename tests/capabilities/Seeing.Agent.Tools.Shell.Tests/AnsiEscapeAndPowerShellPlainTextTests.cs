using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.Shell;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task ExecuteAsync_ShouldStripAnsiFromToolOutput()
    {
        var ansiLine = "\u001B[32;1mMode\u001B[0m  \u001B[44;1m.idea\u001B[0m\n";
        SubprocessSpec? _ = null;
        var bash = CreateBashTool(spec => _ = spec, stdout: ansiLine);
        var result = await bash.ExecuteAsync(BuildArgs("ls"), CreateContext());
        result.Success.Should().BeTrue();
        result.Output.Should().NotContain("\u001B");
        result.Output.Should().NotContain("[32;1m");
        result.Output.Should().Contain("Mode");
        result.Output.Should().Contain(".idea");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInjectNoColorEnv()
    {
        SubprocessSpec? captured = null;
        var bash = CreateBashTool(spec => captured = spec);
        await bash.ExecuteAsync(BuildArgs("echo hi"), CreateContext());
        captured!.Environment["NO_COLOR"].Should().Be("1");
    }

    private static IShellService CreateShellService()
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());
        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Subprocess).Returns(Mock.Of<ISubprocessFactory>());
        return new DefaultShellService(NullLogger<DefaultShellService>.Instance, options.Object, world.Object);
    }

    private static BashTool CreateBashTool(Action<SubprocessSpec> onStart, string stdout = "hello\n")
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());

        var shellService = new Mock<IShellService>();
        shellService.Setup(s => s.SelectShell()).Returns("pwsh");
        shellService.Setup(s => s.PrepareCommand("pwsh", It.IsAny<string>())).Returns((string _, string c) => c);
        shellService.Setup(s => s.BuildArguments("pwsh", It.IsAny<string>())).Returns("-Command \"echo\"");
        shellService.Setup(s => s.GetShellName(It.IsAny<string>())).Returns("pwsh");

        var shellEnv = new Mock<IShellEnvironmentService>();
        shellEnv.Setup(s => s.GetEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var subprocessFactory = new Mock<ISubprocessFactory>();
        subprocessFactory.Setup(f => f.Start(It.IsAny<SubprocessSpec>())).Returns((SubprocessSpec spec) =>
        {
            onStart(spec);
            return new FakeSubprocess(stdout);
        });

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(@"C:\fake");
        world.Setup(w => w.Subprocess).Returns(subprocessFactory.Object);

        return new BashTool(NullLogger<BashTool>.Instance, world.Object, shellService.Object,
            shellEnv.Object, options.Object);
    }

    private static ToolContext CreateContext() => new() { SessionId = "s", CallId = "c" };

    private static JsonElement BuildArgs(string command) =>
        JsonSerializer.SerializeToElement(new { command, description = "test" });

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
