using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Shell;
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
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec, worldCwd: WorldCwd);
        var result = await bash.ExecuteAsync(BuildArgs("echo hi"), ctx);
        result.Success.Should().BeTrue();
        capturedSpec!.WorkingDirectory.Should().Be(WorldCwd);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkdir_ShouldUseProvidedWorkdir()
    {
        SubprocessSpec? capturedSpec = null;
        const string customWorkdir = @"D:\custom\workdir";
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec);
        await bash.ExecuteAsync(BuildArgs("echo hi", customWorkdir), ctx);
        capturedSpec!.WorkingDirectory.Should().Be(customWorkdir);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPutShellEnvIntoSubprocessSpec()
    {
        SubprocessSpec? capturedSpec = null;
        var env = new Dictionary<string, string> { ["MY_VAR"] = "my_value" };
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec, environment: env);
        await bash.ExecuteAsync(BuildArgs("echo hi"), ctx);
        capturedSpec!.Environment["MY_VAR"].Should().Be("my_value");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInjectUtf8OutputEnvDefaults()
    {
        SubprocessSpec? capturedSpec = null;
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec);
        await bash.ExecuteAsync(BuildArgs("echo hi"), ctx);
        capturedSpec!.Environment["PYTHONIOENCODING"].Should().Be("utf-8");
        capturedSpec.Environment["PYTHONUTF8"].Should().Be("1");
        capturedSpec.Environment["LANG"].Should().Be("C.UTF-8");
        capturedSpec.Environment["LC_ALL"].Should().Be("C.UTF-8");
        capturedSpec.Environment["NO_COLOR"].Should().Be("1");
        capturedSpec.Encoding.Should().Be(Encoding.UTF8);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotOverrideExplicitUtf8EnvFromHook()
    {
        SubprocessSpec? capturedSpec = null;
        var env = new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "gbk",
            ["PYTHONUTF8"] = "0",
        };
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec, environment: env);
        await bash.ExecuteAsync(BuildArgs("echo hi"), ctx);
        capturedSpec!.Environment["PYTHONIOENCODING"].Should().Be("gbk");
        capturedSpec.Environment["PYTHONUTF8"].Should().Be("0");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseShellServiceForLaunch()
    {
        SubprocessSpec? capturedSpec = null;
        var (bash, ctx) = CreateBashTool(spec => capturedSpec = spec, shellPath: "/bin/zsh", preparedCommand: "prepared-cmd", arguments: "-c 'prepared-cmd'");
        await bash.ExecuteAsync(BuildArgs("raw-cmd"), ctx);
        capturedSpec!.FileName.Should().Be("/bin/zsh");
        capturedSpec.Arguments.Should().Be("-c 'prepared-cmd'");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEmitRunningProgressViaEventSink()
    {
        var emissions = new List<ToolCallEvent>();
        var (bash, ctx) = CreateBashTool(
            _ => { },
            stdout: "line1\nline2\n",
            onEmit: evt =>
            {
                if (evt is ToolCallEvent tc)
                    emissions.Add(tc);
            });

        var result = await bash.ExecuteAsync(BuildArgs("echo hi"), ctx);
        result.Success.Should().BeTrue();
        emissions.Should().NotBeEmpty();
        emissions.Should().OnlyContain(e => e.Status == ToolCallStatus.Running && e.ToolName == "bash");
        emissions.Last().Output.Should().Contain("line1");
    }

    private static (BashTool Tool, ToolContext Context) CreateBashTool(
        Action<SubprocessSpec> onStart,
        string worldCwd = WorldCwd,
        IReadOnlyDictionary<string, string>? environment = null,
        string shellPath = "/bin/bash",
        string preparedCommand = "echo hi",
        string arguments = "-c 'echo hi'",
        string stdout = "hello\n",
        Action<IMessageEvent>? onEmit = null)
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());

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
            return new FakeSubprocess(stdout);
        });

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(worldCwd);
        world.Setup(w => w.Subprocess).Returns(subprocessFactory.Object);

        IToolEventSink? sink = null;
        if (onEmit != null)
        {
            var mock = new Mock<IToolEventSink>();
            mock.Setup(s => s.EmitAsync(It.IsAny<IMessageEvent>()))
                .Returns((IMessageEvent evt) =>
                {
                    onEmit(evt);
                    return ValueTask.CompletedTask;
                });
            sink = mock.Object;
        }

        var tool = new BashTool(NullLogger<BashTool>.Instance, world.Object, shellService.Object,
            shellEnv.Object, options.Object);
        var context = new ToolContext
        {
            SessionId = "s",
            CallId = "c",
            EventSink = sink
        };
        return (tool, context);
    }

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
