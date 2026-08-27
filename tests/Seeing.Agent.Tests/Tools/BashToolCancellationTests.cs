using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Shell;
using Seeing.Agent.Tools.BuiltIn.Shell;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// BashTool 超时/取消语义测试
/// <para>验证：工具自身超时与用户取消能被准确区分并报告，不互相误报。</para>
/// </summary>
public class BashToolCancellationTests
{
    /// <summary>
    /// 长时间运行命令（跨平台）
    /// </summary>
    private static string LongRunningCommand =>
        OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";

    private static BashTool CreateBashTool(out Mock<IShellService> shellService)
    {
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions());

        shellService = new Mock<IShellService>();
        shellService.Setup(s => s.GetAcceptableShell()).Returns(ResolveShell);
        shellService.Setup(s => s.GetShellName(It.IsAny<string>()))
            .Returns((string s) => Path.GetFileNameWithoutExtension(s).ToLowerInvariant());
        shellService.Setup(s => s.KillProcessTreeAsync(It.IsAny<Process>(), It.IsAny<CancellationToken>()))
            .Callback<Process, CancellationToken>((p, _) =>
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能已退出，忽略
                }
            })
            .Returns(Task.CompletedTask);

        var shellEnv = new Mock<IShellEnvironmentService>();
        shellEnv.Setup(s => s.GetEnvironmentAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.WorkspaceRoot).Returns(Path.GetTempPath());

        return new BashTool(
            NullLogger<BashTool>.Instance,
            shellService.Object,
            shellEnv.Object,
            workspace.Object,
            options.Object);
    }

    private static ToolContext CreateContext(CancellationToken cancellationToken = default) => new()
    {
        SessionId = "test-session",
        CallId = "call-1",
        CancellationToken = cancellationToken
    };

    private static JsonElement BuildArgs(string command, int? timeout = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["description"] = "测试命令"
        };
        if (timeout.HasValue)
            obj["timeout"] = timeout.Value;
        return JsonSerializer.SerializeToElement(obj);
    }

    private static string ResolveShell()
    {
        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "pwsh", "powershell" }
                     : new[] { "bash", "zsh", "sh" })
        {
            var found = FindOnPath(name);
            if (found != null)
                return found;
        }
        return OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : "/bin/sh";
    }

    private static string? FindOnPath(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// 工具自身超时：应报告"命令在超过超时时间...被终止"，而非"用户取消了命令"
    /// </summary>
    [Fact]
    public async Task BashToolTimeout_ShouldReportTimeoutMessage_NotUserCancellation()
    {
        var bash = CreateBashTool(out _);

        var result = await bash.ExecuteAsync(BuildArgs(LongRunningCommand, timeout: 500), CreateContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("命令在超过超时时间 500 毫秒后被终止");
        result.Output.Should().NotContain("用户取消了命令");
        result.Metadata["timedOut"].Should().Be(true);
        result.Metadata["aborted"].Should().Be(false);
    }

    /// <summary>
    /// PowerShell 中文输出：应保持 UTF-8 编码不乱码（非 PowerShell 环境跳过）
    /// </summary>
    [Fact]
    public async Task PowerShellOutput_WithChineseText_ShouldNotBeGarbled()
    {
        var bash = CreateBashTool(out var shellService);
        var shell = shellService.Object.GetAcceptableShell();
        var name = shellService.Object.GetShellName(shell);
        if (name is not ("powershell" or "pwsh"))
            return; // 仅 PowerShell 环境验证

        var result = await bash.ExecuteAsync(BuildArgs("Write-Output \"中文测试\""), CreateContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("中文测试");
    }

    /// <summary>
    /// 用户取消：应报告"用户取消了命令"，而非超时信息
    /// </summary>
    [Fact]
    public async Task UserCancellation_ShouldReportUserCancelled_NotTimeout()
    {
        var bash = CreateBashTool(out _);
        using var cts = new CancellationTokenSource();

        var task = bash.ExecuteAsync(BuildArgs(LongRunningCommand), CreateContext(cts.Token));
        await Task.Delay(300);
        cts.Cancel();

        var result = await task;

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("用户取消了命令");
        result.Output.Should().NotContain("命令在超过超时时间");
        result.Metadata["aborted"].Should().Be(true);
        result.Metadata["timedOut"].Should().Be(false);
    }

    /// <summary>
    /// timeout=0 或负数：应返回 Failure（0ms 无意义），而非立即超时的怪语义。
    /// </summary>
    [Fact]
    public async Task BashTool_TimeoutZero_ShouldRejectInvalid()
    {
        var bash = CreateBashTool(out _);

        var result = await bash.ExecuteAsync(BuildArgs("echo hi", timeout: 0), CreateContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无效的超时值");
    }

    [Fact]
    public async Task BashTool_TimeoutNegative_ShouldRejectInvalid()
    {
        var bash = CreateBashTool(out _);

        var result = await bash.ExecuteAsync(BuildArgs("echo hi", timeout: -100), CreateContext());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("无效的超时值");
    }
}
