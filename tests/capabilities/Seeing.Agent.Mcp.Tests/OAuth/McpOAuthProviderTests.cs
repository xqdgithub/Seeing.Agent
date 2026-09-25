using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

public class McpOAuthProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-mcp-oauth-provider-tests", Guid.NewGuid().ToString("N"));

    public McpOAuthProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private McpOAuthStorage CreateStorage()
        => new(NullLogger<McpOAuthStorage>.Instance,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, "key.bin"));

    private static McpOAuthProvider CreateProvider(
        RecordingHandler handler,
        McpOAuthStorage storage,
        McpOAuthConfig config)
    {
        var tokenClient = new McpOAuthTokenClient(
            NullLogger<McpOAuthTokenClient>.Instance,
            new FakeHttpClientFactory(handler));
        return new McpOAuthProvider(
            NullLogger<McpOAuthProvider>.Instance,
            storage,
            new FakeCallbackServer(),
            tokenClient,
            _ => new McpServerConfig
            {
                Name = "server-1",
                TransportType = McpTransportType.StreamableHttp,
                Url = new Uri("https://mcp.example.com/mcp"),
                OAuth = config
            });
    }

    private static McpOAuthConfig ConfiguredOAuth() => new()
    {
        ClientId = "client-1",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token",
        UsePkce = true
    };

    [Fact]
    public async Task StartAuthAsync_ShouldBuildPkceAuthorizationUrl()
    {
        var config = ConfiguredOAuth();
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), CreateStorage(), config);

        var result = await provider.StartAuthAsync("server-1");

        result.AuthorizationUrl.Should().StartWith("https://auth.example.com/authorize?");
        result.AuthorizationUrl.Should().Contain("client_id=client-1")
            .And.Contain("response_type=code")
            .And.Contain("code_challenge_method=S256")
            .And.Contain("code_challenge=")
            .And.Contain($"state={result.State}")
            .And.Contain("redirect_uri=");
        result.AuthorizationUrl.Should().NotContain("example.com/oauth/authorize?response_type=code&redirect_uri=http%3A%2F%2Flocalhost");
        result.CodeVerifier.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAuthAsync_WhenNoAuthorizationEndpoint_ShouldThrowExplicitError()
    {
        var config = new McpOAuthConfig { TokenEndpoint = "https://auth.example.com/token" };
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), CreateStorage(), config);

        var act = () => provider.StartAuthAsync("server-1");

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*授权端点*");
    }

    [Fact]
    public async Task StartAuthAsync_WhenOAuthDisabled_ShouldThrowExplicitError()
    {
        var config = ConfiguredOAuth();
        config.Disabled = true;
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), CreateStorage(), config);

        var act = () => provider.StartAuthAsync("server-1");

        await act.Should().ThrowAsync<McpOAuthException>();
    }

    [Fact]
    public async Task FinishAuthAsync_WithWrongState_ShouldRejectWithoutHttpCall()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, "{}");
        var provider = CreateProvider(handler, CreateStorage(), ConfiguredOAuth());

        await provider.StartAuthAsync("server-1");
        var result = await provider.FinishAuthAsync("server-1", "the-code", "tampered-state");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("State mismatch");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task FinishAuthAsync_WithValidState_ShouldExchangeCodeAndPersistToken()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"exchanged","refresh_token":"rt-new","token_type":"Bearer","expires_in":3600}""");
        var storage = CreateStorage();
        var provider = CreateProvider(handler, storage, ConfiguredOAuth());

        var start = await provider.StartAuthAsync("server-1");
        var result = await provider.FinishAuthAsync("server-1", "the-code", start.State);

        result.Success.Should().BeTrue();
        result.Status.Should().Be(McpAuthStatus.Authenticated);
        result.Token!.AccessToken.Should().Be("exchanged");
        handler.CallCount.Should().Be(1);

        var persisted = await storage.LoadTokenAsync("server-1");
        persisted!.AccessToken.Should().Be("exchanged");
    }

    [Fact]
    public async Task FinishAuthAsync_WithoutPendingAuth_ShouldFail()
    {
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), CreateStorage(), ConfiguredOAuth());

        var result = await provider.FinishAuthAsync("unknown", "code", "state");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTokenExpiredWithRefreshToken_ShouldRefresh()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"refreshed","refresh_token":"rt-2","token_type":"Bearer","expires_in":3600}""");
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken
        {
            AccessToken = "stale",
            RefreshToken = "rt-1",
            ExpiresIn = -10
        });
        var provider = CreateProvider(handler, storage, ConfiguredOAuth());

        var result = await provider.AuthenticateAsync("server-1");

        result.Success.Should().BeTrue();
        result.Token!.AccessToken.Should().Be("refreshed");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AuthenticateAsync_WhenNoStoredToken_ShouldReturnNotAuthenticated()
    {
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), CreateStorage(), ConfiguredOAuth());

        var result = await provider.AuthenticateAsync("server-1");

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NotAuthenticated);
    }

    [Fact]
    public async Task RefreshTokenAsync_WhenNoRefreshToken_ShouldFailWithoutHttpCall()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, "{}");
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken { AccessToken = "stale", ExpiresIn = -10 });
        var provider = CreateProvider(handler, storage, ConfiguredOAuth());

        var result = await provider.RefreshTokenAsync("server-1");

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAuthAsync_ShouldDeleteStoredToken()
    {
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken { AccessToken = "t", ExpiresIn = 60 });
        var provider = CreateProvider(RecordingHandler.Json(HttpStatusCode.OK, "{}"), storage, ConfiguredOAuth());

        await provider.RemoveAuthAsync("server-1");

        (await provider.HasStoredTokensAsync("server-1")).Should().BeFalse();
        (await provider.GetAuthStatusAsync("server-1")).Should().Be(McpAuthStatus.NotAuthenticated);
    }

    private sealed class FakeCallbackServer : IMcpOAuthCallbackServer
    {
        public int Port { get; set; } = 59123;

        public Task<int> EnsureRunningAsync() => Task.FromResult(Port);

        public string GetCallbackUrl() => $"http://localhost:{Port}/callback";

        public Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout)
            => Task.FromResult(("code", state));
    }
}
