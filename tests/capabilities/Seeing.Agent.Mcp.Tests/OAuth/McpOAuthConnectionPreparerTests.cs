using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// 连接时 OAuth 预处理：OAuth 启用且无有效令牌时，按开关决定自动触发授权或给出可操作提示；
/// 非交互宿主（默认开关关闭）绝不自动打开浏览器阻塞，OAuth 禁用时不干预连接。
/// </summary>
public class McpOAuthConnectionPreparerTests
{
    private static McpServerConfig ServerWith(McpOAuthConfig? oauth) => new()
    {
        Name = "server-1",
        TransportType = McpTransportType.StreamableHttp,
        Url = new Uri("https://mcp.example.com/mcp"),
        OAuth = oauth
    };

    private static (McpOAuthConnectionPreparer Sut, Mock<IMcpOAuthProvider> Provider, Mock<IMcpOAuthAuthorizer> Authorizer)
        CreateSut()
    {
        var provider = new Mock<IMcpOAuthProvider>();
        var authorizer = new Mock<IMcpOAuthAuthorizer>();
        var sut = new McpOAuthConnectionPreparer(
            provider.Object, authorizer.Object, NullLogger<McpOAuthConnectionPreparer>.Instance);
        return (sut, provider, authorizer);
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_WhenOAuthDisabled_ShouldPassWithoutCallingProvider()
    {
        var (sut, provider, authorizer) = CreateSut();
        var server = ServerWith(new McpOAuthConfig { Disabled = true });

        var result = await sut.EnsureAuthorizedAsync("server-1", server);

        result.Success.Should().BeTrue();
        provider.VerifyNoOtherCalls();
        authorizer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_WhenOAuthAbsent_ShouldPass()
    {
        var (sut, provider, authorizer) = CreateSut();

        var result = await sut.EnsureAuthorizedAsync("server-1", ServerWith(null));

        result.Success.Should().BeTrue();
        provider.VerifyNoOtherCalls();
        authorizer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_WhenTokenValid_ShouldNotAuthorize()
    {
        var (sut, provider, authorizer) = CreateSut();
        var server = ServerWith(new McpOAuthConfig());
        provider.Setup(p => p.AuthenticateAsync("server-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(true, McpAuthStatus.Authenticated));

        var result = await sut.EnsureAuthorizedAsync("server-1", server);

        result.Success.Should().BeTrue();
        authorizer.Verify(a => a.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_WhenNoTokenAndAutoAuthorizeDisabled_ShouldReturnActionableHint()
    {
        var (sut, provider, authorizer) = CreateSut();
        var server = ServerWith(new McpOAuthConfig { AutoAuthorize = false });
        provider.Setup(p => p.AuthenticateAsync("server-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(false, McpAuthStatus.NotAuthenticated, "No stored token found"));

        var result = await sut.EnsureAuthorizedAsync("server-1", server);

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
        result.Error.Should().Contain("/mcp-auth server-1");
        authorizer.Verify(a => a.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureAuthorizedAsync_WhenNoTokenAndAutoAuthorizeEnabled_ShouldTriggerAuthorizer()
    {
        var (sut, provider, authorizer) = CreateSut();
        var server = ServerWith(new McpOAuthConfig { AutoAuthorize = true });
        provider.Setup(p => p.AuthenticateAsync("server-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(false, McpAuthStatus.NotAuthenticated, "No stored token found"));
        authorizer.Setup(a => a.AuthorizeAsync("server-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(true, McpAuthStatus.Authenticated));

        var result = await sut.EnsureAuthorizedAsync("server-1", server);

        result.Success.Should().BeTrue();
        authorizer.Verify(a => a.AuthorizeAsync("server-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
