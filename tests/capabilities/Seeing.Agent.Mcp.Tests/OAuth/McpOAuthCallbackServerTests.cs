using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// 回调服务器并发授权：单监听端口按 state 分发回调，多个 pending 授权互不串扰；
/// 无匹配 state 的回调必须被拒绝，绝不误触发其它等待方。
/// </summary>
public class McpOAuthCallbackServerTests
{
    [Fact]
    public async Task WaitForCallbackAsync_ShouldRouteConcurrentCallbacksByState()
    {
        using var server = new McpOAuthCallbackServer(NullLogger<McpOAuthCallbackServer>.Instance);
        using var client = new HttpClient();

        var port = await server.EnsureRunningAsync();
        var callbackUrl = $"http://localhost:{port}/callback";

        var waitingA = server.WaitForCallbackAsync("state-A", TimeSpan.FromSeconds(5));
        var waitingB = server.WaitForCallbackAsync("state-B", TimeSpan.FromSeconds(5));

        // 先回调 B，再回调 A：验证按 state 分发而非按等待注册顺序
        var responseB = await client.GetAsync($"{callbackUrl}?code=code-B&state=state-B");
        ((int)responseB.StatusCode).Should().Be(200);
        (await waitingB).Should().Be(("code-B", "state-B"));
        waitingA.IsCompleted.Should().BeFalse("B 的回调不得误触发 A 的等待");

        var responseA = await client.GetAsync($"{callbackUrl}?code=code-A&state=state-A");
        ((int)responseA.StatusCode).Should().Be(200);
        (await waitingA).Should().Be(("code-A", "state-A"));
    }

    [Fact]
    public async Task WaitForCallbackAsync_WithUnknownState_ShouldRejectAndLeavePendingWaitsIntact()
    {
        using var server = new McpOAuthCallbackServer(NullLogger<McpOAuthCallbackServer>.Instance);
        using var client = new HttpClient();

        var port = await server.EnsureRunningAsync();
        var callbackUrl = $"http://localhost:{port}/callback";

        var waiting = server.WaitForCallbackAsync("known", TimeSpan.FromSeconds(5));

        var rejected = await client.GetAsync($"{callbackUrl}?code=wrong&state=unknown");
        ((int)rejected.StatusCode).Should().Be(400);
        waiting.IsCompleted.Should().BeFalse("无匹配 state 的回调必须被拒绝，不得完成在途等待");

        await client.GetAsync($"{callbackUrl}?code=good&state=known");
        (await waiting).Should().Be(("good", "known"));
    }

    [Fact]
    public async Task EnsureRunningAsync_Twice_ShouldReuseSinglePortAndSupportSequentialAuthorizations()
    {
        using var server = new McpOAuthCallbackServer(NullLogger<McpOAuthCallbackServer>.Instance);
        using var client = new HttpClient();

        var port1 = await server.EnsureRunningAsync();
        var port2 = await server.EnsureRunningAsync();
        port2.Should().Be(port1, "单监听端口仅绑定一次");

        var callbackUrl = $"http://localhost:{port1}/callback";

        var first = server.WaitForCallbackAsync("state-1", TimeSpan.FromSeconds(5));
        await client.GetAsync($"{callbackUrl}?code=code-1&state=state-1");
        (await first).Should().Be(("code-1", "state-1"));

        // 第二次授权：不得复用上一次已完成的等待源
        var second = server.WaitForCallbackAsync("state-2", TimeSpan.FromSeconds(5));
        await client.GetAsync($"{callbackUrl}?code=code-2&state=state-2");
        (await second).Should().Be(("code-2", "state-2"));
    }

    [Fact]
    public async Task WaitForCallbackAsync_WhenTimeout_ShouldThrowTimeoutException()
    {
        using var server = new McpOAuthCallbackServer(NullLogger<McpOAuthCallbackServer>.Instance);
        await server.EnsureRunningAsync();

        var act = () => server.WaitForCallbackAsync("never", TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
    }
}
