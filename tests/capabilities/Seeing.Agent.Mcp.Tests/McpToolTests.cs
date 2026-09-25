using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// McpTool 取消令牌透传：执行委托须收到调用上下文的 CancellationToken。
/// </summary>
public class McpToolTests
{
    /// <summary>恒允许授权器：使授权门通过，聚焦取消令牌透传。</summary>
    private sealed class AllowingAuthorizer : IPermissionAuthorizer
    {
        public string SessionId => "s1";

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
            => Task.FromResult(new PermissionResolution
            {
                RequestId = "req",
                SessionId = request.SessionId,
                Decision = PermissionEffect.Allow,
                ResolvedBy = PermissionResolvedBy.User
            });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPropagateContextCancellationTokenToExecutor()
    {
        // Arrange
        CancellationToken captured = default;
        using var document = JsonDocument.Parse("{}");

        var tool = new McpTool(
            "demo",
            "echo",
            "回显",
            document.RootElement,
            (_, _, cancellationToken) =>
            {
                captured = cancellationToken;
                return Task.FromResult(new McpToolResult { Content = "ok" });
            });

        using var cts = new CancellationTokenSource();
        var context = new ToolContext { CancellationToken = cts.Token, PermissionAuthorizer = new AllowingAuthorizer() };

        // Act
        var result = await tool.ExecuteAsync(document.RootElement, context);

        // Assert
        captured.Should().Be(cts.Token);
        result.Success.Should().BeTrue();
        result.Output.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelledToken_ShouldPassCancelledToken()
    {
        // Arrange
        CancellationToken captured = default;
        using var document = JsonDocument.Parse("{}");

        var tool = new McpTool(
            "demo",
            "echo",
            "回显",
            document.RootElement,
            (_, _, cancellationToken) =>
            {
                captured = cancellationToken;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new McpToolResult { Content = "ok" });
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new ToolContext { CancellationToken = cts.Token, PermissionAuthorizer = new AllowingAuthorizer() };

        // Act
        var result = await tool.ExecuteAsync(document.RootElement, context);

        // Assert：取消被上层信封兜底为失败结果，但令牌已真实透传
        captured.IsCancellationRequested.Should().BeTrue();
        result.Success.Should().BeFalse();
    }
}
