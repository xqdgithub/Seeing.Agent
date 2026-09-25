using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// MCP OAuth 授权闭环测试：StartAuthAsync → 打开浏览器 → 等待回调 → FinishAuthAsync 交换并持久化令牌。
/// 无宿主交互能力（无法打开浏览器）或回调超时时必须显式失败，绝不伪造令牌。
/// </summary>
public class McpOAuthAuthorizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-mcp-oauth-authorizer-tests", Guid.NewGuid().ToString("N"));

    public McpOAuthAuthorizerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private McpOAuthStorage CreateStorage()
        => new(NullLogger<McpOAuthStorage>.Instance,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, "key.bin"));

    private static McpOAuthConfig ConfiguredOAuth() => new()
    {
        ClientId = "client-1",
        AuthorizationEndpoint = "https://auth.example.com/authorize",
        TokenEndpoint = "https://auth.example.com/token",
        UsePkce = true
    };

    private static (McpOAuthProvider Provider, RecordingHandler Handler) CreateProvider(
        McpOAuthStorage storage,
        McpOAuthConfig config,
        string tokenJson)
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, tokenJson);
        var tokenClient = new McpOAuthTokenClient(
            NullLogger<McpOAuthTokenClient>.Instance,
            new FakeHttpClientFactory(handler));
        var provider = new McpOAuthProvider(
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
        return (provider, handler);
    }

    private static McpOAuthAuthorizer CreateAuthorizer(
        McpOAuthProvider provider,
        IMcpOAuthCallbackServer callbackServer,
        IBrowserLauncher browserLauncher)
        => new(provider, callbackServer, browserLauncher,
            NullLogger<McpOAuthAuthorizer>.Instance, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task AuthorizeAsync_HappyPath_ShouldOpenBrowserAwaitCallbackAndPersistToken()
    {
        var storage = CreateStorage();
        var (provider, handler) = CreateProvider(storage, ConfiguredOAuth(),
            """{"access_token":"at-1","refresh_token":"rt-1","token_type":"Bearer","expires_in":3600}""");
        var browser = new RecordingBrowserLauncher(success: true);
        var callback = new FakeCallbackServer
        {
            OnWait = state => ("the-code", state)
        };
        var authorizer = CreateAuthorizer(provider, callback, browser);

        var result = await authorizer.AuthorizeAsync("server-1");

        result.Success.Should().BeTrue();
        result.Status.Should().Be(McpAuthStatus.Authenticated);
        browser.LastUrl.Should().StartWith("https://auth.example.com/authorize?")
            .And.Contain("code_challenge_method=S256");
        callback.WaitCalled.Should().BeTrue();
        handler.CallCount.Should().Be(1);
        (await storage.LoadTokenAsync("server-1"))!.AccessToken.Should().Be("at-1");
    }

    [Fact]
    public async Task AuthorizeAsync_WhenBrowserCannotOpen_ShouldFailExplicitlyWithoutExchangingToken()
    {
        var storage = CreateStorage();
        var (provider, handler) = CreateProvider(storage, ConfiguredOAuth(), "{}");
        var browser = new RecordingBrowserLauncher(success: false);
        var callback = new FakeCallbackServer
        {
            OnWait = _ => ("the-code", "irrelevant")
        };
        var authorizer = CreateAuthorizer(provider, callback, browser);

        var result = await authorizer.AuthorizeAsync("server-1");

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
        result.Error.Should().Contain("https://auth.example.com/authorize")
            .And.Contain("手动");
        callback.WaitCalled.Should().BeFalse();
        handler.CallCount.Should().Be(0);
        (await storage.TokenExistsAsync("server-1")).Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_WhenCallbackTimesOut_ShouldFailExplicitlyWithoutToken()
    {
        var storage = CreateStorage();
        var (provider, handler) = CreateProvider(storage, ConfiguredOAuth(), "{}");
        var browser = new RecordingBrowserLauncher(success: true);
        var callback = new FakeCallbackServer { ThrowTimeout = true };
        var authorizer = CreateAuthorizer(provider, callback, browser);

        var result = await authorizer.AuthorizeAsync("server-1");

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
        result.Error.Should().Contain("超时");
        handler.CallCount.Should().Be(0);
        (await storage.TokenExistsAsync("server-1")).Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_WhenServerNotConfiguredForOAuth_ShouldFailExplicitly()
    {
        var storage = CreateStorage();
        var (provider, _) = CreateProvider(storage, new McpOAuthConfig(), "{}");
        var browser = new RecordingBrowserLauncher(success: true);
        var callback = new FakeCallbackServer();
        var authorizer = CreateAuthorizer(provider, callback, browser);

        var result = await authorizer.AuthorizeAsync("server-1");

        result.Success.Should().BeFalse();
        result.Status.Should().Be(McpAuthStatus.NeedsAuthorization);
        result.Error.Should().NotBeNullOrWhiteSpace();
        browser.LastUrl.Should().BeNull();
        callback.WaitCalled.Should().BeFalse();
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        private readonly bool _success;

        public RecordingBrowserLauncher(bool success) => _success = success;

        public string? LastUrl { get; private set; }

        public bool TryOpen(string url)
        {
            LastUrl = url;
            return _success;
        }
    }

    private sealed class FakeCallbackServer : IMcpOAuthCallbackServer
    {
        public Func<string, (string Code, string State)>? OnWait { get; set; }

        public bool ThrowTimeout { get; set; }

        public bool WaitCalled { get; private set; }

        public Task<int> EnsureRunningAsync() => Task.FromResult(59123);

        public string GetCallbackUrl() => "http://localhost:59123/callback";

        public Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout)
        {
            WaitCalled = true;
            if (ThrowTimeout)
                throw new TimeoutException("callback timeout");

            return Task.FromResult(OnWait is null ? ("code", state) : OnWait(state));
        }
    }
}
