using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Shell;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// <c>bash</c> 前台执行：启动 <c>kind=process</c> 活动 → 等待终态 → 读活动输出 → 作为工具结果返回。
/// <para>工具自身不再管理进程、超时与流式输出，全部由 manager 裁决。</para>
/// </summary>
public class BashToolForegroundTests
{
    private const string Workdir = @"D:\fake\foreground-workdir";
    private const string SessionId = "session-fg";
    private const string ExecutionId = "exec-fg";
    private const string CallId = "call-fg";
    private const int TimeoutMs = 5_000;

    [Fact]
    public async Task Foreground_Completed_ShouldReturnOutputAndExitCode()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = FakeBackgroundActivityManager.SinglePage("hello\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo hi"), context);

        // Assert：输出与退出码来自活动终态
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("hello");
        result.Metadata["exit"].Should().Be(0);
        result.Metadata["timedOut"].Should().Be(false);
        result.Metadata["aborted"].Should().Be(false);
        result.BackgroundActivityId.Should().Be("act-1", "成功也返回活动 id：超出读回上限时模型可用 activity_output 补读");
        result.BackgroundDetached.Should().BeFalse("前台正常完成不是转后台，UI 必须取完整工具结果");

        // 前台请求：Origin=ToolForeground、不通知、超时即工具 timeout
        var request = manager.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(ProcessBackgroundActivityLauncher.ProcessKind);
        request.OwnerSessionId.Should().Be(SessionId);
        request.OwnerExecutionId.Should().Be(ExecutionId);
        request.OriginToolCallId.Should().Be(CallId);
        request.WorkingDirectory.Should().Be(Workdir);
        request.Origin.Should().Be(BackgroundActivityOrigin.ToolForeground);
        request.NotifyPolicy.Should().Be(BackgroundActivityNotifyPolicy.None);
        request.Timeout.Should().Be(TimeSpan.FromMilliseconds(TimeoutMs));
        request.Payload!.Value.GetProperty("command").GetString().Should().Be("echo hi");
        request.Payload!.Value.GetProperty("workdir").GetString().Should().Be(Workdir);

        manager.RequesterSessionIds.Should().Equal(SessionId);
        manager.Waits.Should().ContainSingle();
        manager.Waits[0].ActivityId.Should().Be("act-1");
        manager.Waits[0].Timeout.Should().Be(TimeSpan.FromMilliseconds(TimeoutMs + 500), "等待终态要略长于命令超时");
        manager.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Foreground_TimedOut_ShouldCancelActivity_AndFail()
    {
        // Arrange：等待超时，活动仍在运行且已产生部分输出
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.WaitOutcome(
                id, BackgroundActivityWaitOutcome.TimedOut, BackgroundActivityStatus.Running),
            OutputHandler = FakeBackgroundActivityManager.SinglePage("partial line\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("sleep 60", TimeoutMs), context);

        // Assert：超时以工具失败表达，已产生的输出一并返回
        result.Success.Should().BeFalse();
        result.Error.Should().Be($"命令在超过超时时间 {TimeoutMs} 毫秒后被终止");
        result.Output.Should().Contain("partial line");
        result.Output.Should().Contain("bash_metadata");
        result.Metadata["timedOut"].Should().Be(true);
        result.Metadata["aborted"].Should().Be(false);

        var cancel = manager.Cancels.Should().ContainSingle().Subject;
        cancel.ActivityId.Should().Be("act-1");
        cancel.Reason.Should().Be(BackgroundActivityCancelReason.Timeout);
        cancel.RequesterSessionId.Should().Be(SessionId);
    }

    [Fact]
    public async Task Foreground_Cancelled_ShouldCancelActivity_AndFail()
    {
        // Arrange：调用方取消（不取消活动本身由工具负责收尾）
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.WaitOutcome(
                id, BackgroundActivityWaitOutcome.CallerCancelled, BackgroundActivityStatus.Running),
            OutputHandler = FakeBackgroundActivityManager.SinglePage("partial\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("sleep 60", TimeoutMs), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("已取消");
        result.Output.Should().Contain("partial");
        result.Metadata["aborted"].Should().Be(true);
        result.Metadata["timedOut"].Should().Be(false);

        var cancel = manager.Cancels.Should().ContainSingle().Subject;
        cancel.Reason.Should().Be(BackgroundActivityCancelReason.User);
        cancel.ActivityId.Should().Be("act-1");
    }

    [Fact]
    public async Task Foreground_Detached_ShouldReturnSuccessWithActivityId()
    {
        // Arrange：用户在等待期间点了「转后台」
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.WaitOutcome(
                id, BackgroundActivityWaitOutcome.Detached, BackgroundActivityStatus.Running)
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("sleep 60", TimeoutMs), context);

        // Assert：成功返回句柄，活动继续跑，工具不再取消它
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().Be("act-1");
        result.BackgroundDetached.Should().BeTrue("转后台是唯一强类型标记：活动继续跑，输出取活动实时尾部");
        result.Output.Should().Contain("act-1");
        result.Output.Should().Contain("activity_*");
        result.Metadata.Should().NotContainKey("backgroundActivityId", "活动 id 只走强类型字段");
        manager.Cancels.Should().BeEmpty("转后台不是取消");
    }

    [Fact]
    public async Task Foreground_WhenActivityNotFound_ShouldFail()
    {
        // Arrange：越权/不存在时 manager 返回空快照
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = _ => new BackgroundActivityWaitResult(BackgroundActivityWaitOutcome.TimedOut, null)
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo hi"), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("后台活动不存在或不属于当前会话");
        manager.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Foreground_NonZeroExit_ShouldStillSucceed()
    {
        // Arrange：非零退出码由 launcher 上报为 Succeeded + ExitCode，不是工具失败
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.Completed(
                FakeBackgroundActivityManager.Snapshot(id, BackgroundActivityStatus.Succeeded, exitCode: 2)),
            OutputHandler = FakeBackgroundActivityManager.SinglePage("boom\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("exit 2"), context);

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("boom");
        result.Metadata["exit"].Should().Be(2);
    }

    [Fact]
    public async Task Foreground_WhenActivityFailedWithTimeout_ShouldFailWithTimeoutMessage()
    {
        // Arrange：launcher 侧超时（子进程看门狗）把活动落为 Failed + timeout 错误码
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.Completed(
                FakeBackgroundActivityManager.Snapshot(
                    id,
                    BackgroundActivityStatus.Failed,
                    errorCode: ProcessBackgroundActivityLauncher.TimeoutErrorCode,
                    errorMessage: "命令超过 5000 毫秒后被终止。")),
            OutputHandler = FakeBackgroundActivityManager.SinglePage("partial\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("sleep 60", TimeoutMs), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be($"命令在超过超时时间 {TimeoutMs} 毫秒后被终止");
        result.Output.Should().Contain("partial");
        result.Metadata["timedOut"].Should().Be(true);
    }

    [Fact]
    public async Task Foreground_WhenActivityFailed_ShouldReturnLauncherError()
    {
        // Arrange：启动失败（如可执行文件不存在）
        var manager = new FakeBackgroundActivityManager
        {
            WaitHandler = id => FakeBackgroundActivityManager.Completed(
                FakeBackgroundActivityManager.Snapshot(
                    id,
                    BackgroundActivityStatus.Failed,
                    errorCode: ProcessBackgroundActivityLauncher.LaunchFailedErrorCode,
                    errorMessage: "spawn failed")),
            OutputHandler = FakeBackgroundActivityManager.SinglePage("")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("nope"), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("spawn failed");
        result.Metadata["timedOut"].Should().Be(false);
    }

    [Fact]
    public async Task Foreground_ShouldStripAnsiFromOutput()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = FakeBackgroundActivityManager.SinglePage(
                "\u001B[32;1mMode\u001B[0m  \u001B[44;1m.idea\u001B[0m\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("ls"), context);

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().NotContain("\u001B");
        result.Output.Should().NotContain("[32;1m");
        result.Output.Should().Contain("Mode");
        result.Output.Should().Contain(".idea");
    }

    [Fact]
    public async Task Foreground_LargeOutput_ShouldNotBeTruncatedAt30k()
    {
        // Arrange：40k 字符远超旧的 30k 内联上限
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = FakeBackgroundActivityManager.SinglePage(new string('x', 40_000))
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("big"), context);

        // Assert：不再内联截断——超长部分交给 ToolOutputLimiter 统一外溢（产出 outputPath）
        result.Success.Should().BeTrue();
        result.Output.Should().HaveLength(40_000, "30k 硬截断会让 ToolOutputLimiter 永不触发，也不产生 outputPath");
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task Foreground_ShouldReturnActivityId_ForFollowUpRead()
    {
        // Arrange：正常完成的前台命令
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = FakeBackgroundActivityManager.SinglePage("hello\n")
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo hi"), context);

        // Assert：成功也交出活动 id（读回有 256KB 上限，剩余输出只能靠 activity_output 补读）
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().Be("act-1");
        result.BackgroundDetached.Should().BeFalse("前台正常完成不是转后台，UI 仍取工具结果");
    }

    [Fact]
    public async Task Foreground_TruncatedOutput_ShouldMarkMetadata()
    {
        // Arrange：活动侧已截断（落盘上限），读回内容不是完整输出
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = (_, offset, _) => new BackgroundActivityOutputPage(
                "partial", offset + 7, EndOfStream: true, Truncated: true)
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("big"), context);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata["truncated"].Should().Be(true);

        // Arrange：读取达到单次读回上限（256KB），流尾尚未到达
        var capped = new FakeBackgroundActivityManager
        {
            OutputHandler = (_, offset, maxBytes) => new BackgroundActivityOutputPage(
                new string('y', maxBytes), offset + maxBytes, EndOfStream: false, Truncated: false)
        };
        var (cappedTool, cappedContext) = CreateTool(capped);

        // Act
        var cappedResult = await cappedTool.ExecuteAsync(BuildArgs("huge"), cappedContext);

        // Assert
        cappedResult.Success.Should().BeTrue();
        cappedResult.Metadata["truncated"].Should().Be(true, "达到 256KB 读回上限即视为截断，剩余输出用 activity_output 补读");
        cappedResult.BackgroundActivityId.Should().Be("act-1");
    }

    [Fact]
    public async Task Foreground_ShouldReadOutputInPages()
    {
        // Arrange：两页输出，游标前进直到 EndOfStream
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = (_, offset, _) => offset switch
            {
                0 => new BackgroundActivityOutputPage("aaa", 3, EndOfStream: false, Truncated: false),
                3 => new BackgroundActivityOutputPage("bbb", 6, EndOfStream: true, Truncated: false),
                _ => new BackgroundActivityOutputPage(string.Empty, offset, EndOfStream: true, Truncated: false)
            }
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo aaabbb"), context);

        // Assert
        result.Output.Should().Be("aaabbb");
        manager.OutputReads.Should().HaveCount(2);
        manager.OutputReads[0].Offset.Should().Be(0);
        manager.OutputReads[1].Offset.Should().Be(3);
        manager.OutputReads.Should().OnlyContain(r => r.MaxBytes <= 32 * 1024);
    }

    [Fact]
    public async Task Foreground_StalledOutputCursor_ShouldStopReading()
    {
        // Arrange：分页游标不前进（异常实现）也不得死循环
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = (_, offset, _) => new BackgroundActivityOutputPage("x", offset, EndOfStream: false, Truncated: false)
        };
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("stalled"), context);

        // Assert
        result.Success.Should().BeTrue();
        manager.OutputReads.Should().HaveCount(1);
    }

    [Fact]
    public async Task Foreground_DangerousCommand_ShouldBeRejected_WithoutStartingActivity()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager();
        var (tool, context) = CreateTool(manager, new ShellOptions { EnableCommandGuard = true });

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("rm -rf /"), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("命令被拒绝执行");
        manager.Requests.Should().BeEmpty("危险命令不得启动活动");
    }

    [Fact]
    public async Task Foreground_InvalidTimeout_ShouldBeRejected()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager();
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo hi", timeoutMs: 0), context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无效的超时值");
        manager.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Foreground_WithoutWorkdir_ShouldUseContextWorkingDirectory()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager();
        var (tool, context) = CreateTool(manager);

        // Act
        var result = await tool.ExecuteAsync(BuildArgs("echo hi", useWorkdir: false), context);

        // Assert
        result.Success.Should().BeTrue();
        var request = manager.Requests.Should().ContainSingle().Subject;
        request.WorkingDirectory.Should().Be(context.WorkingDirectory);
        request.Payload!.Value.GetProperty("workdir").GetString().Should().Be(context.WorkingDirectory);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static (BashTool Tool, ToolContext Context) CreateTool(
        IBackgroundActivityManager manager, ShellOptions? shellOptions = null)
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(shellOptions ?? new ShellOptions());

        var shell = new Mock<IShellService>();
        shell.Setup(s => s.SelectShell()).Returns("/bin/bash");
        shell.Setup(s => s.GetShellName(It.IsAny<string>())).Returns("bash");

        var tool = new BashTool(
            NullLogger<BashTool>.Instance,
            shell.Object,
            options.Object,
            AllowAllWorkspacePathGate.Instance,
            manager);

        var context = new ToolContext
        {
            SessionId = SessionId,
            CallId = CallId,
            ExecutionId = ExecutionId,
            WorkingDirectory = Workdir
        };

        return (tool, context);
    }

    private static JsonElement BuildArgs(
        string command, int timeoutMs = TimeoutMs, bool useWorkdir = true)
    {
        var values = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["description"] = "test",
            ["timeout"] = timeoutMs
        };

        if (useWorkdir) values["workdir"] = Workdir;

        return JsonSerializer.SerializeToElement(values);
    }
}
