using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
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
    public async Task BackgroundTrue_ShouldNotWait()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager();
        var (tool, context) = CreateTool(manager, moduleEnabled: true);

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

        // Assert：立即返回句柄，不进入等待
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().Be("act-1");
        result.Output.Should().Contain("act-1");
        result.Metadata.Should().NotContainKey("backgroundActivityId", "活动 id 只走强类型字段");
        manager.Waits.Should().BeEmpty("background=true 不等待终态");

        manager.RequesterSessionIds.Should().Equal(SessionId);
        var request = manager.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(ProcessBackgroundActivityLauncher.ProcessKind);
        request.Title.Should().Be("后台任务");
        request.OwnerSessionId.Should().Be(SessionId);
        request.OwnerExecutionId.Should().Be(ExecutionId);
        request.OriginToolCallId.Should().Be(CallId);
        request.WorkingDirectory.Should().Be(Workdir);
        request.Origin.Should().Be(BackgroundActivityOrigin.ToolBackground);
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
        manager.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundTrue_WithSameDedupeKey_ShouldReuseExistingActivity()
    {
        // Arrange：第二次启动命中去重，manager 返回同一活动并标记 IsExisting
        var manager = new FakeBackgroundActivityManager();
        manager.StartHandler = (ordinal, _) => ordinal == 1
            ? Task.FromResult(FakeBackgroundActivityManager.NewHandle("act-first"))
            : Task.FromResult(FakeBackgroundActivityManager.NewHandle("act-first") with { IsExisting = true });

        var (tool, context) = CreateTool(manager, moduleEnabled: true);
        var arguments = BuildArgs(new()
        {
            ["command"] = "dotnet test",
            ["workdir"] = Workdir,
            ["background"] = true
        });

        // Act
        var first = await tool.ExecuteAsync(arguments, context);
        var second = await tool.ExecuteAsync(arguments, context);

        // Assert：复用不是失败，但回执必须说清「没有重新执行」
        first.Success.Should().BeTrue();
        first.BackgroundActivityId.Should().Be("act-first");
        first.Output.Should().Contain("命令已转交后台活动执行");

        second.Success.Should().BeTrue();
        second.BackgroundActivityId.Should().Be("act-first");
        second.Output.Should().Contain("复用");
        second.Output.Should().Contain("act-first");
        second.Output.Should().NotContain("命令已转交后台活动执行");

        manager.Requests.Should().HaveCount(2);
        manager.Requests[0].DedupeKey.Should().Be(manager.Requests[1].DedupeKey);
    }

    [Fact]
    public async Task BackgroundTrue_WhenModuleDisabled_ShouldFail()
    {
        // Arrange
        var manager = new FakeBackgroundActivityManager();
        var (tool, context) = CreateTool(manager, moduleEnabled: false);

        // Act
        var result = await tool.ExecuteAsync(
            BuildArgs(new() { ["command"] = "sleep 60", ["workdir"] = Workdir, ["background"] = true }),
            context);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("background");
        manager.Requests.Should().BeEmpty("模块禁用时不得启动活动");
    }

    [Fact]
    public async Task BackgroundFalse_ShouldIgnoreActivityTimeout_AndUseForegroundOrigin()
    {
        // Arrange：activity_timeout 仅在 background=true 时生效，这里给非法值也不得影响前台执行
        var manager = new FakeBackgroundActivityManager
        {
            OutputHandler = FakeBackgroundActivityManager.SinglePage("hi\n")
        };
        var (tool, context) = CreateTool(manager, moduleEnabled: true);

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

        // Assert：走前台等待路径，超时取 timeout 而非 activity_timeout
        result.Success.Should().BeTrue();
        result.BackgroundActivityId.Should().Be("act-1", "成功的前台命令同样交出活动 id");
        var request = manager.Requests.Should().ContainSingle().Subject;
        request.Origin.Should().Be(BackgroundActivityOrigin.ToolForeground);
        request.Timeout.Should().Be(TimeSpan.FromMilliseconds(5_000));
        manager.Waits.Should().ContainSingle();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static (BashTool Tool, ToolContext Context) CreateTool(
        IBackgroundActivityManager manager,
        bool moduleEnabled)
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());

        var shell = new Mock<IShellService>();
        shell.Setup(s => s.SelectShell()).Returns("/bin/bash");
        shell.Setup(s => s.GetShellName(It.IsAny<string>())).Returns("bash");

        var modules = new Mock<IModuleCatalog>();
        modules.Setup(m => m.IsEnabled("background")).Returns(moduleEnabled);

        var tool = new BashTool(
            NullLogger<BashTool>.Instance,
            shell.Object,
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

        return (tool, context);
    }

    private static JsonElement BuildArgs(Dictionary<string, object?> values)
        => JsonSerializer.SerializeToElement(values);
}
