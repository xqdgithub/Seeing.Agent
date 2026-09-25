using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Transport;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>
/// Session 销毁时 ACP Terminal 级联清理。
/// </summary>
public class AcpSessionLifecycleHookTerminalTests
{
    [Fact]
    public async Task ExecuteAsync_OnSessionDestroyed_ShouldReleaseSessionTerminals()
    {
        // Arrange
        var sessionManager = new Mock<ISessionManager>();
        var store = new AcpSessionStore(sessionManager.Object, NullLogger<AcpSessionStore>.Instance);
        var owner = new AcpConnectionOwner(() => throw new NotSupportedException("未激活"));
        var bridge = new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance);
        var hook = new AcpSessionLifecycleHook(
            store, owner, bridge, NullLogger<AcpSessionLifecycleHook>.Instance);

        var (command, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new List<string> { "/c", "ping", "-n", "30", "127.0.0.1" })
            : ("/bin/sh", new List<string> { "-c", "sleep 30" });

        await bridge.CreateTerminalAsync(command, "sess-hook", Directory.GetCurrentDirectory(), args);
        bridge.ActiveTerminalCount.Should().Be(1);

        try
        {
            // Act
            var payload = HookPayload.FireAndForget(HookRegistry.SessionDestroyed, "sess-hook");
            var result = await hook.ExecuteAsync(payload);

            // Assert
            result.Continue.Should().BeTrue();
            bridge.ActiveTerminalCount.Should().Be(0);
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }
}
