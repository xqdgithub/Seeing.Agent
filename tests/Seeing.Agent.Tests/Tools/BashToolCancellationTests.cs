using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Shell;
using Seeing.IO.Local;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class BashToolCancellationTests
{
    private static string LongRunningCommand =>
        OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";

    private static BashTool CreateBashTool(out IShellService shellService)
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());
        shellService = new DefaultShellService(NullLogger<DefaultShellService>.Instance, options.Object);
        var shellEnv = new Mock<IShellEnvironmentService>();
        shellEnv.Setup(s => s.GetEnvironmentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        return new BashTool(NullLogger<BashTool>.Instance, new LocalExecutionWorld(), shellService, shellEnv.Object, options.Object);
    }

    private static ToolContext CreateContext(CancellationToken ct = default) => new() { SessionId = "s", CallId = "c", CancellationToken = ct };

    private static JsonElement BuildArgs(string command, int? timeout = null)
    {
        var obj = new Dictionary<string, object?> { ["command"] = command, ["description"] = "test" };
        if (timeout.HasValue) obj["timeout"] = timeout.Value;
        return JsonSerializer.SerializeToElement(obj);
    }

    [Fact]
    public async Task BashToolTimeout_ShouldReportTimeoutMessage_NotUserCancellation()
    {
        var bash = CreateBashTool(out _);
        var result = await bash.ExecuteAsync(BuildArgs(LongRunningCommand, 500), CreateContext());
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("命令在超过超时时间 500 毫秒后被终止");
        result.Metadata["timedOut"].Should().Be(true);
        result.Metadata["aborted"].Should().Be(false);
    }

    [Fact]
    public async Task PowerShellOutput_WithChineseText_ShouldNotBeGarbled()
    {
        var bash = CreateBashTool(out var shellService);
        if (shellService.GetShellName(shellService.SelectShell()) is not ("powershell" or "pwsh")) return;
        var result = await bash.ExecuteAsync(BuildArgs("Write-Output \"中文测试\""), CreateContext());
        result.Output.Should().Contain("中文测试");
    }

    [Fact]
    public async Task UserCancellation_ShouldReportUserCancelled_NotTimeout()
    {
        var bash = CreateBashTool(out _);
        using var cts = new CancellationTokenSource();
        var task = bash.ExecuteAsync(BuildArgs(LongRunningCommand), CreateContext(cts.Token));
        await Task.Delay(300);
        cts.Cancel();
        var result = await task;
        result.Output.Should().Contain("用户取消了命令");
        result.Metadata["aborted"].Should().Be(true);
    }

    [Fact]
    public async Task BashTool_TimeoutZero_ShouldRejectInvalid()
    {
        var bash = CreateBashTool(out _);
        (await bash.ExecuteAsync(BuildArgs("echo hi", 0), CreateContext())).Success.Should().BeFalse();
    }

    [Fact]
    public async Task BashTool_LargeOutput_ShouldPassFullOutput()
    {
        var bash = CreateBashTool(out var shellService);
        if (OperatingSystem.IsWindows() && shellService.GetShellName(shellService.SelectShell()) is not ("powershell" or "pwsh")) return;
        var cmd = OperatingSystem.IsWindows() ? "Write-Output ('x' * 40000)" : "printf 'x%.0s' {1..40000}";
        var result = await bash.ExecuteAsync(BuildArgs(cmd), CreateContext());
        result.Output.Length.Should().BeGreaterThan(30_000);
    }
}
