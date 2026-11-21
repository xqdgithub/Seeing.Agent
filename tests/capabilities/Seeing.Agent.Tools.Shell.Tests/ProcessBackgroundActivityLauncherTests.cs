using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.Shell;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class ProcessBackgroundActivityLauncherTests
{
    private const string Workdir = "/tmp/fake-workdir";

    [Fact]
    public async Task Launch_ShouldReturnImmediately_WhileProcessStillRunning()
    {
        var process = new FakeSubprocess();
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        process.HasExited.Should().BeFalse("假子进程被挂起，测试断言启动不等待退出");
        launch.Execution.IsCompleted.Should().BeFalse("执行任务仍在运行");
        reporter.Completed.Should().BeNull();
        reporter.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_ShouldKillProcessTree()
    {
        var process = new FakeSubprocess();
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process);
        using var handleCts = new CancellationTokenSource();
        var handle = CreateHandle(reporter, handleCts.Token);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // manager 的取消顺序：先落 Cancelled（同时取消句柄 token），再通知 launcher。
        await handleCts.CancelAsync();
        await launcher.CancelAsync(handle, BackgroundActivityCancelReason.User)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        process.KillCount.Should().Be(1);
        process.KilledEntireTree.Should().BeTrue();
        launch.Execution.IsCompletedSuccessfully.Should().BeTrue("取消后执行任务必须自然收尾");
        reporter.Completed.Should().BeNull("终态由 manager 裁决，launcher 不得重复上报");
        reporter.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_ShouldReturn_WhenProcessDoesNotExit()
    {
        // Kill 成功但进程就是不退出（僵尸进程 / 句柄被占）：取消必须受超时约束，不能永久挂住。
        var time = new FakeTimeProvider();
        var process = new FakeSubprocess(exitOnKill: false);
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process, timeProvider: time, exitWaitTimeout: TimeSpan.FromSeconds(5));
        using var handleCts = new CancellationTokenSource();
        var handle = CreateHandle(reporter, handleCts.Token);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await handleCts.CancelAsync();

        var cancel = launcher.CancelAsync(handle, BackgroundActivityCancelReason.User);
        cancel.IsCompleted.Should().BeFalse("进程未退出，执行任务仍在等，取消不该立即返回");

        // 用假时钟把「等退出」的超时推过去（不真实等待）。
        time.Advance(TimeSpan.FromSeconds(5));

        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        process.KillCount.Should().Be(1);
        reporter.Completed.Should().BeNull("终态由 manager 裁决");
        reporter.Failure.Should().BeNull();

        // 收尾路径自身也已放弃等待：执行任务不得继续挂住。
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LongRunning_ShouldEmitHeartbeat()
    {
        var time = new FakeTimeProvider();
        var process = new FakeSubprocess();
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process, timeProvider: time);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // 每次推进 30 秒并等心跳落地后再推进下一次：时钟是假的，不真实等待。
        for (var i = 1; i <= 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            var expected = i;
            await WaitUntilAsync(
                () => reporter.HeartbeatCount >= expected,
                $"第 {expected} 次心跳应在 30 秒 × {expected} 后上报");
        }

        reporter.HeartbeatCount.Should().BeGreaterThanOrEqualTo(2);

        process.Exit(0);
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var afterExit = reporter.HeartbeatCount;
        time.Advance(TimeSpan.FromMinutes(5));
        reporter.HeartbeatCount.Should().Be(afterExit, "进程退出后必须停止心跳");
    }

    [Fact]
    public async Task Output_ShouldStreamToReporter()
    {
        var process = new FakeSubprocess(stdout: ["line1", "line2"], stderr: ["boom"]);
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        process.Exit(0);
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var text = reporter.OutputText;
        text.Should().Contain("line1\n");
        text.Should().Contain("line2\n");
        text.Should().Contain("boom\n", "stderr 也逐行流入活动输出");
    }

    [Fact]
    public async Task Exit_ShouldCompleteWithExitCode()
    {
        var process = new FakeSubprocess(stdout: ["done"]);
        var reporter = new FakeReporter();
        var launcher = CreateLauncher(process);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        process.Exit(7);
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        reporter.Failure.Should().BeNull();
        reporter.Completed.Should().NotBeNull();
        reporter.Completed!.Kind.Should().Be(BackgroundActivityResultKind.ProcessExit);
        reporter.Completed.ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task Launch_ShouldStartHostBoundSubprocess_WithPayloadWorkdir()
    {
        var process = new FakeSubprocess();
        var reporter = new FakeReporter();
        SubprocessSpec? captured = null;
        var launcher = CreateLauncher(process, spec => captured = spec);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(
            CreateContext(handle, workdir: Workdir),
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.WorkingDirectory.Should().Be(Workdir);
        captured.Lifecycle.Should().Be(SubprocessLifecycle.HostBound);
        captured.ArgumentList.Should().Equal("-c", "prepared:echo hi");

        process.Exit(0);
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Launch_ShouldInjectUtf8OutputEnvDefaults()
    {
        var process = new FakeSubprocess();
        var reporter = new FakeReporter();
        SubprocessSpec? captured = null;
        var launcher = CreateLauncher(process, spec => captured = spec);
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Environment["PYTHONIOENCODING"].Should().Be("utf-8");
        captured.Environment["PYTHONUTF8"].Should().Be("1");
        captured.Environment["LANG"].Should().Be("C.UTF-8");
        captured.Environment["LC_ALL"].Should().Be("C.UTF-8");
        captured.Environment["NO_COLOR"].Should().Be("1");
        captured.Encoding.Should().Be(Encoding.UTF8);

        process.Exit(0);
        await launch.Execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Launch_WhenStartThrows_ShouldFailWithLaunchFailed()
    {
        var reporter = new FakeReporter();
        var launcher = CreateFailingLauncher();
        var handle = CreateHandle(reporter);

        var launch = await launcher.LaunchAsync(CreateContext(handle), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        launch.Execution.IsCompletedSuccessfully.Should().BeTrue();
        reporter.Completed.Should().BeNull();
        reporter.Failure.Should().NotBeNull();
        reporter.Failure!.Value.Code.Should().Be(ProcessBackgroundActivityLauncher.LaunchFailedErrorCode);
    }

    [Fact]
    public void Launcher_ShouldDeclareProcessKindAndCapabilities()
    {
        var launcher = CreateLauncher(new FakeSubprocess());

        launcher.Kind.Should().Be("process");
        launcher.Capabilities.Should().HaveFlag(BackgroundActivityCapabilities.CanCancel);
        launcher.Capabilities.Should().HaveFlag(BackgroundActivityCapabilities.CanWait);
        launcher.Capabilities.Should().HaveFlag(BackgroundActivityCapabilities.CanStreamOutput);
        launcher.Capabilities.Should().HaveFlag(BackgroundActivityCapabilities.SignalsCompletion);
    }

    // ─── 装配 ────────────────────────────────────────────────────────────────

    private static ProcessBackgroundActivityLauncher CreateLauncher(
        FakeSubprocess process,
        Action<SubprocessSpec>? onStart = null,
        TimeProvider? timeProvider = null,
        TimeSpan? exitWaitTimeout = null)
    {
        var factory = new Mock<ISubprocessFactory>();
        factory.Setup(f => f.Start(It.IsAny<SubprocessSpec>()))
            .Returns((SubprocessSpec spec) =>
            {
                onStart?.Invoke(spec);
                return process;
            });

        return new ProcessBackgroundActivityLauncher(
            CreateWorld(factory.Object),
            CreateShellService(),
            CreateShellEnvironment(),
            NullLogger<ProcessBackgroundActivityLauncher>.Instance,
            timeProvider,
            exitWaitTimeout);
    }

    private static ProcessBackgroundActivityLauncher CreateFailingLauncher()
    {
        var factory = new Mock<ISubprocessFactory>();
        factory.Setup(f => f.Start(It.IsAny<SubprocessSpec>()))
            .Throws(new InvalidOperationException("spawn failed"));

        return new ProcessBackgroundActivityLauncher(
            CreateWorld(factory.Object),
            CreateShellService(),
            CreateShellEnvironment(),
            NullLogger<ProcessBackgroundActivityLauncher>.Instance);
    }

    private static IExecutionWorld CreateWorld(ISubprocessFactory factory)
    {
        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(Workdir);
        world.Setup(w => w.Subprocess).Returns(factory);
        return world.Object;
    }

    private static IShellService CreateShellService()
    {
        var shell = new Mock<IShellService>();
        shell.Setup(s => s.SelectShell()).Returns("/bin/bash");
        shell.Setup(s => s.PrepareCommand("/bin/bash", It.IsAny<string>()))
            .Returns((string _, string command) => "prepared:" + command);
        shell.Setup(s => s.BuildArgumentList("/bin/bash", It.IsAny<string>()))
            .Returns((string _, string command) => ["-c", command]);
        shell.Setup(s => s.BuildArguments("/bin/bash", It.IsAny<string>())).Returns(string.Empty);
        return shell.Object;
    }

    private static IShellEnvironmentService CreateShellEnvironment()
    {
        var env = new Mock<IShellEnvironmentService>();
        env.Setup(e => e.GetEnvironmentAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return env.Object;
    }

    private static BackgroundActivityHandle CreateHandle(FakeReporter reporter, CancellationToken? token = null)
        => new("act_test", reporter, token ?? default);

    private static BackgroundActivityLaunchContext CreateContext(
        BackgroundActivityHandle handle,
        string command = "echo hi",
        string? workdir = Workdir)
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["command"] = command,
            ["workdir"] = workdir,
        });

        var request = new BackgroundActivityStartRequest
        {
            Kind = ProcessBackgroundActivityLauncher.ProcessKind,
            Title = "echo hi",
            OwnerSessionId = "session-1",
            OwnerExecutionId = "exec-1",
            Payload = payload,
            WorkingDirectory = Workdir,
        };

        return new BackgroundActivityLaunchContext(handle, request, Workdir);
    }

    /// <summary>
    /// 条件轮询：命中即返回，上限兜底。轮询间隔属 E5（等待实现细节），不是「等状态就绪」的固定等待断言。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        const int maxAttempts = 500;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"等待条件超时：{because}");
    }

    // ─── fake ────────────────────────────────────────────────────────────────

    /// <summary>可控子进程：<see cref="WaitForExitAsync"/> 挂起到 <see cref="Exit"/> / <see cref="Kill"/> 为止。</summary>
    private sealed class FakeSubprocess : ISubprocess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _exitOnKill;

        /// <param name="exitOnKill">为 <see langword="false"/> 时模拟「Kill 成功但进程就是不退出」。</param>
        public FakeSubprocess(string[]? stdout = null, string[]? stderr = null, bool exitOnKill = true)
        {
            _exitOnKill = exitOnKill;
            StandardOutput = new LineReader(stdout ?? []);
            StandardError = new LineReader(stderr ?? []);
        }

        public int Id => 4242;

        public TextReader StandardOutput { get; }

        public TextReader StandardError { get; }

        public bool HasExited => _exit.Task.IsCompleted;

        public int ExitCode { get; private set; }

        public int KillCount { get; private set; }

        public bool KilledEntireTree { get; private set; }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => await _exit.Task.WaitAsync(cancellationToken);

        public void Kill(bool entireTree = true)
        {
            KillCount++;
            KilledEntireTree = entireTree;
            if (_exitOnKill)
                _exit.TrySetResult();
        }

        public void Exit(int exitCode)
        {
            ExitCode = exitCode;
            _exit.TrySetResult();
        }

        public void Dispose() { }
    }

    /// <summary>预置行的流读取器：读完返回 <see langword="null"/>，支持读取消。</summary>
    private sealed class LineReader : TextReader
    {
        private readonly object _gate = new();
        private readonly Queue<string> _lines;

        public LineReader(IEnumerable<string> lines) => _lines = new Queue<string>(lines);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                return new ValueTask<string?>(_lines.Count > 0 ? _lines.Dequeue() : null);
            }
        }
    }

    private sealed class FakeReporter : IBackgroundActivityReporter
    {
        private readonly object _gate = new();
        private readonly List<byte> _output = [];
        private int _heartbeatCount;

        public BackgroundActivityResult? Completed { get; private set; }

        public (string Code, string Message)? Failure { get; private set; }

        public int HeartbeatCount => Volatile.Read(ref _heartbeatCount);

        public string OutputText
        {
            get
            {
                lock (_gate)
                {
                    return Encoding.UTF8.GetString(_output.ToArray());
                }
            }
        }

        public void ReportStarted() { }

        public void ReportProgress(BackgroundActivityProgress progress) { }

        public void Heartbeat() => Interlocked.Increment(ref _heartbeatCount);

        public Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _output.AddRange(chunk.ToArray());
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync(BackgroundActivityResult result, CancellationToken ct = default)
        {
            Completed = result;
            return Task.CompletedTask;
        }

        public Task FailAsync(string errorCode, string errorMessage, CancellationToken ct = default)
        {
            Failure = (errorCode, errorMessage);
            return Task.CompletedTask;
        }
    }
}
