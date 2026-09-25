using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Acp.Terminal;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>
/// ACP Terminal 桥生命周期：Exited 延迟清理、按 session 级联释放、TTL 兜底。
/// </summary>
public class AcpTerminalBridgeTests
{
    [Fact]
    public async Task DiRegistration_ShouldResolveUsingDefaultRetention()
    {
        // 构造函数带有可选保留参数，须能被 DI 以默认值解析
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<AcpTerminalBridge>>(NullLogger<AcpTerminalBridge>.Instance);
        services.AddSingleton<AcpTerminalBridge>();

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AcpTerminalBridge>().Should().NotBeNull();
    }

    [Fact]
    public async Task ReleaseBySession_ShouldReleaseOnlyMatchingSession()
    {
        // Arrange：两个会话各持一个长驻终端
        var bridge = new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance);
        var (command, args) = LongRunningCommand();
        var workingDirectory = Directory.GetCurrentDirectory();

        var first = await bridge.CreateTerminalAsync(command, "session-1", workingDirectory, args);
        var second = await bridge.CreateTerminalAsync(command, "session-2", workingDirectory, args);

        bridge.ActiveTerminalCount.Should().Be(2);

        try
        {
            // Act：销毁 session-1
            var released = bridge.ReleaseBySession("session-1");

            // Assert：仅 session-1 的终端被回收
            released.Should().Be(1);
            bridge.ActiveTerminalCount.Should().Be(1);
            var surviving = await bridge.TerminalOutputAsync(
                "session-2", second.TerminalId, TestContext.Current.CancellationToken);
            surviving.Exited.Should().BeFalse();
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task Exited_AfterRetention_ShouldReleaseProcessAndOutput()
    {
        // Arrange：短保留窗口便于验证 Exited 后的延迟清理
        var bridge = new AcpTerminalBridge(
            NullLogger<AcpTerminalBridge>.Instance,
            exitRetention: TimeSpan.FromMilliseconds(50),
            idleTtl: TimeSpan.FromMinutes(10));
        var (command, args) = QuickExitCommand();

        var created = await bridge.CreateTerminalAsync(
            command, "session-exit", Directory.GetCurrentDirectory(), args);

        try
        {
            // Act：等待进程退出并等待延迟清理
            await bridge.WaitForTerminalExitAsync("session-exit", created.TerminalId, TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => Task.FromResult(bridge.ActiveTerminalCount == 0),
                TimeSpan.FromSeconds(10));

            // Assert：进程句柄与输出均已回收
            bridge.ActiveTerminalCount.Should().Be(0);
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminalOutput_AfterExitBeforeRetention_ShouldStillReturnOutput()
    {
        // Arrange：保留窗口足够长，退出后仍可读取输出
        var bridge = new AcpTerminalBridge(
            NullLogger<AcpTerminalBridge>.Instance,
            exitRetention: TimeSpan.FromMinutes(5),
            idleTtl: TimeSpan.FromMinutes(10));
        var (command, args) = EchoCommand();

        var created = await bridge.CreateTerminalAsync(
            command, "session-echo", Directory.GetCurrentDirectory(), args);

        try
        {
            await bridge.WaitForTerminalExitAsync("session-echo", created.TerminalId, TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                async () => (await bridge.TerminalOutputAsync(
                    "session-echo", created.TerminalId, TestContext.Current.CancellationToken))
                    .Stdout?.Contains("hello", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(10));

            var output = await bridge.TerminalOutputAsync("session-echo", created.TerminalId, TestContext.Current.CancellationToken);
            output.Exited.Should().BeTrue();
            output.Stdout.Should().Contain("hello");
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static (string Command, List<string> Args) QuickExitCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", new List<string> { "/c", "exit", "0" })
        : ("/bin/sh", new List<string> { "-c", "exit 0" });

    private static (string Command, List<string> Args) LongRunningCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", new List<string> { "/c", "ping", "-n", "30", "127.0.0.1" })
        : ("/bin/sh", new List<string> { "-c", "sleep 30" });

    private static (string Command, List<string> Args) EchoCommand() => OperatingSystem.IsWindows()
        ? ("cmd.exe", new List<string> { "/c", "echo", "hello" })
        : ("/bin/sh", new List<string> { "-c", "echo hello" });
}
