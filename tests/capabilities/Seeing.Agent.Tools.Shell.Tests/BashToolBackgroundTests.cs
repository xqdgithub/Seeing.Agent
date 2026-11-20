using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Shell;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// <c>bash background=true</c>：只做前置校验与请求装配，立即返回活动 id（不等命令退出）。
/// </summary>
public class BashToolBackgroundTests
{
    private const string Workdir = @"D:\fake\background-workdir";
    private const string SessionId = "session-bg";
    private const string ExecutionId = "exec-bg";
    private const string CallId = "call-bg";

    [Fact]
    public async Task BackgroundTrue_ShouldReturnActivityId_AndNotWaitForExit()
    {
        // Arrange
        var manager = new FakeBackgroundManager();
        var (tool, context, subprocessStarts) = CreateTool(manager, moduleEnabled: true);

        var arguments = BuildArgs(new()
        {
            ["command"] = "sleep 60",
            ["workdir"] = Workdir,
            ["description"] = "后台任务",
            ["background"] = true,
            ["timeout"] = 0, // 被 activity_timeout 取代：即使非法也不影响转后台
            ["activity_timeout"] = 600_000
        });

        // Act
        var result = await tool.ExecuteAsync(arguments, context);

        // Assert：立即返回句柄，不启动前台子进程
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().Be("act-1");
        result.Output.Should().Contain("act-1");
        result.Metadata.Should().NotContainKey("backgroundActivityId", "活动 id 只走强类型字段");
        subprocessStarts().Should().Be(0, "background=true 不得在前台起进程");

        manager.RequesterSessionIds.Should().Equal(SessionId);
        var request = manager.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(ProcessBackgroundActivityLauncher.ProcessKind);
        request.Title.Should().Be("后台任务");
        request.OwnerSessionId.Should().Be(SessionId);
        request.OwnerExecutionId.Should().Be(ExecutionId);
        request.OriginToolCallId.Should().Be(CallId);
        request.WorkingDirectory.Should().Be(Workdir);
        request.DedupeStrategy.Should().Be(BackgroundActivityDedupeStrategy.Reject);
        request.Timeout.Should().Be(TimeSpan.FromMilliseconds(600_000));

        request.Payload.Should().NotBeNull();
        var payload = request.Payload!.Value;
        payload.ValueKind.Should().Be(JsonValueKind.Object);
        payload.GetProperty("command").GetString().Should().Be("sleep 60");
        payload.GetProperty("workdir").GetString().Should().Be(Workdir);
        payload.GetProperty("timeoutMs").GetInt32().Should().Be(600_000);

        var expectedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("sleep 60\n" + Workdir)));
        request.DedupeKey.Should().Be(expectedKey);

        // 同一 command/workdir 的第二次调用必须算出同一个去重键（与 activity_timeout 无关）
        var secondResult = await tool.ExecuteAsync(
            BuildArgs(new()
            {
                ["command"] = "sleep 60",
                ["workdir"] = Workdir,
                ["background"] = true,
                ["activity_timeout"] = 1_000
            }),
            context);

        secondResult.Success.Should().BeTrue();
        manager.Requests.Should().HaveCount(2);
        manager.Requests[1].DedupeKey.Should().Be(expectedKey, "去重键只由 command + workdir 决定");
        subprocessStarts().Should().Be(0);
    }

    [Fact]
    public async Task BackgroundTrue_WithSameDedupeKey_ShouldRejectSecond()
    {
        // Arrange
        var manager = new FakeBackgroundManager();
        manager.StartHandler = (ordinal, _) => ordinal == 1
            ? Task.FromResult(NewHandle("act-first"))
            : throw new InvalidOperationException("去重命中：已有活动 act-first 正在执行同一命令。");

        var (tool, context, _) = CreateTool(manager, moduleEnabled: true);
        var arguments = BuildArgs(new()
        {
            ["command"] = "dotnet test",
            ["workdir"] = Workdir,
            ["background"] = true
        });

        // Act
        var first = await tool.ExecuteAsync(arguments, context);
        var second = await tool.ExecuteAsync(arguments, context);

        // Assert
        first.Success.Should().BeTrue();
        first.BackgroundActivityId.Should().Be("act-first");

        second.Success.Should().BeFalse();
        second.BackgroundActivityId.Should().BeNull();
        second.Error.Should().Contain("act-first", "拒绝提示必须指向在途活动，便于调用方改用 activity_wait");

        manager.Requests.Should().HaveCount(2);
        manager.Requests[0].DedupeKey.Should().Be(manager.Requests[1].DedupeKey);
    }

    [Fact]
    public async Task BackgroundTrue_WhenManagerMissing_ShouldFail()
    {
        // Arrange：宿主未接线后台活动（manager = null）
        var (tool, context, subprocessStarts) = CreateTool(manager: null, moduleEnabled: true);

        // Act
        var result = await tool.ExecuteAsync(
            BuildArgs(new() { ["command"] = "sleep 60", ["workdir"] = Workdir, ["background"] = true }),
            context);

        // Assert
        result.Success.Should().BeFalse();
        result.BackgroundActivityId.Should().BeNull();
        result.Error.Should().Contain("后台活动不可用");
        subprocessStarts().Should().Be(0);
    }

    [Fact]
    public async Task BackgroundTrue_WhenModuleDisabled_ShouldFail()
    {
        // Arrange
        var manager = new FakeBackgroundManager();
        var (tool, context, subprocessStarts) = CreateTool(manager, moduleEnabled: false);

        // Act
        var result = await tool.ExecuteAsync(
            BuildArgs(new() { ["command"] = "sleep 60", ["workdir"] = Workdir, ["background"] = true }),
            context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("background");
        manager.Requests.Should().BeEmpty("模块禁用时不得启动活动");
        subprocessStarts().Should().Be(0);
    }

    [Fact]
    public async Task BackgroundFalse_ShouldIgnoreActivityTimeout()
    {
        // Arrange：activity_timeout 仅在 background=true 时生效，这里给非法值也不得影响前台执行
        var manager = new FakeBackgroundManager();
        var (tool, context, subprocessStarts) = CreateTool(manager, moduleEnabled: true);

        var arguments = BuildArgs(new()
        {
            ["command"] = "echo hi",
            ["workdir"] = Workdir,
            ["timeout"] = 5_000,
            ["activity_timeout"] = 0,
            ["background"] = false
        });

        // Act
        var result = await tool.ExecuteAsync(arguments, context);

        // Assert
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().BeNull();
        manager.Requests.Should().BeEmpty();
        subprocessStarts().Should().Be(1, "background=false 仍走原前台路径");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static (BashTool Tool, ToolContext Context, Func<int> SubprocessStarts) CreateTool(
        IBackgroundActivityManager? manager,
        bool moduleEnabled)
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());

        var shell = new Mock<IShellService>();
        shell.Setup(s => s.SelectShell()).Returns("/bin/bash");
        shell.Setup(s => s.PrepareCommand(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string command) => command);
        shell.Setup(s => s.BuildArguments(It.IsAny<string>(), It.IsAny<string>())).Returns("-c 'echo hi'");
        shell.Setup(s => s.BuildArgumentList(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((IReadOnlyList<string>?)null);
        shell.Setup(s => s.GetShellName(It.IsAny<string>())).Returns("bash");

        var environment = new Mock<IShellEnvironmentService>();
        environment.Setup(e => e.GetEnvironmentAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var starts = 0;
        var subprocessFactory = new Mock<ISubprocessFactory>();
        subprocessFactory.Setup(f => f.Start(It.IsAny<SubprocessSpec>()))
            .Returns(() =>
            {
                starts++;
                return new FakeSubprocess();
            });

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(Workdir);
        world.Setup(w => w.Subprocess).Returns(subprocessFactory.Object);

        var modules = new Mock<IModuleCatalog>();
        modules.Setup(m => m.IsEnabled("background")).Returns(moduleEnabled);

        var tool = new BashTool(
            NullLogger<BashTool>.Instance,
            world.Object,
            shell.Object,
            environment.Object,
            options.Object,
            AllowAllWorkspacePathGate.Instance,
            manager,
            modules.Object);

        var context = new ToolContext
        {
            SessionId = SessionId,
            CallId = CallId,
            ExecutionId = ExecutionId,
            WorkingDirectory = Workdir
        };

        return (tool, context, () => starts);
    }

    private static JsonElement BuildArgs(Dictionary<string, object?> values)
        => JsonSerializer.SerializeToElement(values);

    private static BackgroundActivityHandle NewHandle(string activityId)
        => new(activityId, new NoopReporter(), CancellationToken.None);

    private sealed class FakeSubprocess : ISubprocess
    {
        public int Id => 1;
        public TextReader StandardOutput { get; } = new StringReader("hi\n");
        public TextReader StandardError { get; } = new StringReader("");
        public bool HasExited => true;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Kill(bool entireTree = true) { }
        public void Dispose() { }
    }

    private sealed class NoopReporter : IBackgroundActivityReporter
    {
        public void ReportStarted() { }
        public void ReportProgress(BackgroundActivityProgress progress) { }
        public void Heartbeat() { }
        public Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(BackgroundActivityResult result, CancellationToken ct = default) => Task.CompletedTask;
        public Task FailAsync(string errorCode, string errorMessage, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>记录收到的启动请求；<see cref="StartHandler"/> 可按调用序返回或抛出。</summary>
    private sealed class FakeBackgroundManager : IBackgroundActivityManager
    {
        public Func<int, BackgroundActivityStartRequest, Task<BackgroundActivityHandle>> StartHandler { get; set; }
            = (ordinal, _) => Task.FromResult(NewHandle($"act-{ordinal}"));

        public List<BackgroundActivityStartRequest> Requests { get; } = [];

        public List<string> RequesterSessionIds { get; } = [];

        public Task<BackgroundActivityHandle> StartAsync(
            BackgroundActivityStartRequest request,
            string requesterSessionId,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            RequesterSessionIds.Add(requesterSessionId);
            return StartHandler(Requests.Count, request);
        }

        public BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId) => null;

        public IReadOnlyList<BackgroundActivitySnapshot> List(
            BackgroundActivityQuery query, string requesterSessionId) => [];

        public Task<bool> CancelAsync(
            string activityId,
            BackgroundActivityCancelReason reason,
            string requesterSessionId,
            CancellationToken ct = default) => Task.FromResult(false);

        public Task<BackgroundActivityWaitResult> WaitAsync(
            string activityId,
            TimeSpan timeout,
            string requesterSessionId,
            CancellationToken ct = default)
            => Task.FromResult(new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.TimedOut, null));

        public Task<BackgroundActivityOutputPage> ReadOutputAsync(
            string activityId,
            long offset,
            int maxBytes,
            string requesterSessionId,
            CancellationToken ct = default)
            => Task.FromResult(new BackgroundActivityOutputPage(string.Empty, offset, EndOfStream: true, Truncated: false));

        public void MarkResultObserved(string activityId, string requesterSessionId) { }
    }
}
