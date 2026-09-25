using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

public class McpOAuthTokenClientTests
{
    private static McpOAuthTokenClient CreateClient(RecordingHandler handler)
        => new(NullLogger<McpOAuthTokenClient>.Instance, new FakeHttpClientFactory(handler));

    private static McpOAuthConfig Config() => new()
    {
        ClientId = "client-1",
        ClientSecret = "secret-1",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token"
    };

    [Fact]
    public async Task ExchangeCodeAsync_ShouldPostAuthorizationCodeFormAndParseToken()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at-1","refresh_token":"rt-1","token_type":"Bearer","expires_in":3600,"scope":"read"}""");
        var client = CreateClient(handler);

        var token = await client.ExchangeCodeAsync(
            Config(), "code-1", "http://localhost:9999/callback", "verifier-1", CancellationToken.None);

        handler.LastRequestUri.Should().Be(new Uri("https://auth.example.com/token"));
        handler.LastBody.Should().Contain("grant_type=authorization_code")
            .And.Contain("code=code-1")
            .And.Contain("code_verifier=verifier-1")
            .And.Contain("client_id=client-1")
            .And.Contain("redirect_uri=");

        token.AccessToken.Should().Be("at-1");
        token.RefreshToken.Should().Be("rt-1");
        token.ExpiresIn.Should().Be(3600);
        token.Scope.Should().Be("read");
        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ShouldPostRefreshTokenGrant()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at-2","token_type":"Bearer","expires_in":1800}""");
        var client = CreateClient(handler);

        var token = await client.RefreshAsync(Config(), "rt-1", CancellationToken.None);

        handler.LastBody.Should().Contain("grant_type=refresh_token")
            .And.Contain("refresh_token=rt-1")
            .And.Contain("client_id=client-1");
        token.AccessToken.Should().Be("at-2");
        token.ExpiresIn.Should().Be(1800);
        // 刷新响应未返回新的 refresh_token 时保留原值
        token.RefreshToken.Should().Be("rt-1");
    }

    [Fact]
    public async Task ExchangeCodeAsync_WhenServerReturnsError_ShouldThrowMcpOAuthException()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"bad code"}""");
        var client = CreateClient(handler);

        var act = () => client.ExchangeCodeAsync(Config(), "bad", "http://localhost/cb", "v", CancellationToken.None);

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*400*");
    }

    [Fact]
    public async Task ExchangeCodeAsync_WithEnvReferenceClientSecret_ShouldResolveFromEnvironment()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at-1","expires_in":60}""");
        var client = CreateClient(handler);

        var name = "SEEING_MCP_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "resolved-secret");
        try
        {
            var config = Config();
            config.ClientSecret = $"env:{name}";

            await client.ExchangeCodeAsync(
                config, "code-1", "http://localhost:9999/callback", "verifier-1", CancellationToken.None);

            handler.LastBody.Should().Contain("client_secret=resolved-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task ExchangeCodeAsync_WhenNoTokenEndpointConfigured_ShouldThrow()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, "{}");
        var client = CreateClient(handler);

        var act = () => client.ExchangeCodeAsync(
            new McpOAuthConfig(), "code", "http://localhost/cb", "v", CancellationToken.None);

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*Token 端点*");
    }

    [Fact]
    public async Task ExchangeCodeAsync_WhenResponseMissingAccessToken_ShouldThrow()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, """{"token_type":"Bearer"}""");
        var client = CreateClient(handler);

        var act = () => client.ExchangeCodeAsync(
            Config(), "code", "http://localhost/cb", "v", CancellationToken.None);

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*access_token*");
    }
}
