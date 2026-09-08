using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Shell;
using Seeing.Agent.Tools.BuiltIn.Shell;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class BashToolSubprocessTests
{
    private const string WorldCwd = @"C:\fake\world\cwd";

    [Fact]
    public async Task ExecuteAsync_WithoutWorkdir_ShouldUseWorldCwd()
    {
        SubprocessSpec? capturedSpec = null;
        var bash = CreateBashTool(spec => capturedSpec = spec, worldCwd: WorldCwd);
        var result = await bash.ExecuteAsync(BuildArgs("echo hi"), CreateContext());
        result.Success.Should().BeTrue();
        capturedSpec!.WorkingDirectory.Should().Be(WorldCwd);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkdir_ShouldUseProvidedWorkdir()
    {
        SubprocessSpec? capturedSpec = null;
        const string customWorkdir = @"D:\custom\workdir";
        var bash = CreateBashTool(spec => capturedSpec = spec);
        await bash.ExecuteAsync(BuildArgs("echo hi", customWorkdir), CreateContext());
        capturedSpec!.WorkingDirectory.Should().Be(customWorkdir);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPutShellEnvIntoSubprocessSpec()
    {
        SubprocessSpec? capturedSpec = null;
        var env = new Dictionary<string, string> { ["MY_VAR"] = "my_value" };
        var bash = CreateBashTool(spec => capturedSpec = spec, environment: env);
        await bash.ExecuteAsync(BuildArgs("echo hi"), CreateContext());
        capturedSpec!.Environment["MY_VAR"].Should().Be("my_value");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseShellServiceForLaunch()
    {
        SubprocessSpec? capturedSpec = null;
        var bash = CreateBashTool(spec => capturedSpec = spec, shellPath: "/bin/zsh", preparedCommand: "prepared-cmd", arguments: "-c 'prepared-cmd'");
        await bash.ExecuteAsync(BuildArgs("raw-cmd"), CreateContext());
        capturedSpec!.FileName.Should().Be("/bin/zsh");
        capturedSpec.Arguments.Should().Be("-c 'prepared-cmd'");
    }

    private static BashTool CreateBashTool(
        Action<SubprocessSpec> onStart,
        string worldCwd = WorldCwd,
        IReadOnlyDictionary<string, string>? environment = null,
        string shellPath = "/bin/bash",
        string preparedCommand = "echo hi",
        string arguments = "-c 'echo hi'")
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions());

        var shellService = new Mock<IShellService>();
        shellService.Setup(s => s.SelectShell()).Returns(shellPath);
        shellService.Setup(s => s.PrepareCommand(shellPath, It.IsAny<string>())).Returns(preparedCommand);
        shellService.Setup(s => s.BuildArguments(shellPath, preparedCommand)).Returns(arguments);
        shellService.Setup(s => s.GetShellName(It.IsAny<string>()))
            .Returns((string s) => Path.GetFileNameWithoutExtension(s).ToLowerInvariant());

        var shellEnv = new Mock<IShellEnvironmentService>();
        var envDict = environment != null
            ? new Dictionary<string, string>(environment)
            : new Dictionary<string, string>();
        shellEnv.Setup(s => s.GetEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envDict);

        var subprocessFactory = new Mock<ISubprocessFactory>();
        subprocessFactory.Setup(f => f.Start(It.IsAny<SubprocessSpec>())).Returns((SubprocessSpec spec) =>
        {
            onStart(spec);
            return new FakeSubprocess("hello\n");
        });

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(worldCwd);
        world.Setup(w => w.Subprocess).Returns(subprocessFactory.Object);

        return new BashTool(NullLogger<BashTool>.Instance, world.Object, shellService.Object,
            shellEnv.Object, options.Object);
    }

    private static ToolContext CreateContext() => new() { SessionId = "s", CallId = "c" };

    private static JsonElement BuildArgs(string command, string? workdir = null)
    {
        var obj = new Dictionary<string, object?> { ["command"] = command, ["description"] = "test" };
        if (workdir != null) obj["workdir"] = workdir;
        return JsonSerializer.SerializeToElement(obj);
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
