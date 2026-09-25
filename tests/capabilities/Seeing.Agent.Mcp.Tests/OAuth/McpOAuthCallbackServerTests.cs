using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// 回调服务器可重复授权：同进程内第二次授权必须重置回调等待源，
/// 否则复用已完成的 TaskCompletionSource 会用旧回调结果触发 state 校验失败。
/// </summary>
public class McpOAuthCallbackServerTests
{
    [Fact]
    public async Task EnsureRunningAsync_Twice_ShouldResetCallbackSourceForSecondAuthorization()
    {
        using var server = new McpOAuthCallbackServer(NullLogger<McpOAuthCallbackServer>.Instance);
        using var client = new HttpClient();

        var port = await server.EnsureRunningAsync();
        var callbackUrl = $"http://localhost:{port}/callback";

        await client.GetAsync($"{callbackUrl}?code=code-1&state=state-1");
        var first = await server.WaitForCallbackAsync(TimeSpan.FromSeconds(5));
        first.Should().Be(("code-1", "state-1"));

        await server.EnsureRunningAsync();
        await client.GetAsync($"{callbackUrl}?code=code-2&state=state-2");
        var second = await server.WaitForCallbackAsync(TimeSpan.FromSeconds(5));

        second.Should().Be(("code-2", "state-2"));
    }
}
