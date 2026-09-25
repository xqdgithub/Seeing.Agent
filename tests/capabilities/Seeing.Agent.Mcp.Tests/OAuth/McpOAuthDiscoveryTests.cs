using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// OAuth 元数据发现（RFC 9728 / RFC 8414）与动态客户端注册（RFC 7591）：
/// 端点在显式配置缺失时从 server URL 自动发现补全；显式配置优先；发现失败回退明确错误；
/// 动态注册结果按 server 缓存，避免重复注册。
/// </summary>
public class McpOAuthDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-mcp-oauth-discovery-tests", Guid.NewGuid().ToString("N"));

    public McpOAuthDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private McpOAuthStorage CreateStorage()
        => new(NullLogger<McpOAuthStorage>.Instance,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, "key.bin"));

    private McpOAuthClientRegistrationStore CreateRegistrationStore()
        => new(NullLogger<McpOAuthClientRegistrationStore>.Instance,
            storageDirectory: Path.Combine(_root, "registrations"));

    private static McpOAuthProvider CreateProvider(
        HttpMessageHandler handler,
        McpOAuthStorage storage,
        McpServerConfig server,
        McpOAuthClientRegistrationStore registrationStore)
    {
        var factory = new FakeHttpClientFactory(handler);
        var tokenClient = new McpOAuthTokenClient(NullLogger<McpOAuthTokenClient>.Instance, factory);
        var discovery = new McpOAuthDiscovery(NullLogger<McpOAuthDiscovery>.Instance, factory);
        var registrar = new McpOAuthClientRegistrar(
            NullLogger<McpOAuthClientRegistrar>.Instance, factory, registrationStore);

        return new McpOAuthProvider(
            NullLogger<McpOAuthProvider>.Instance,
            storage,
            new FakeCallbackServer(),
            tokenClient,
            _ => server,
            discovery,
            registrar);
    }

    private static McpServerConfig ServerWith(McpOAuthConfig oauth) => new()
    {
        Name = "server-1",
        TransportType = McpTransportType.StreamableHttp,
        Url = new Uri("https://mcp.example.com/mcp"),
        OAuth = oauth
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static RoutingHandler StandardHandler() => new(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("oauth-protected-resource"))
            return Json(HttpStatusCode.OK, """{"authorization_servers":["https://auth.example.com"]}""");
        if (url.Contains("oauth-authorization-server"))
            return Json(HttpStatusCode.OK,
                """{"authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token","registration_endpoint":"https://auth.example.com/register"}""");
        if (url.EndsWith("/register", StringComparison.Ordinal))
            return Json(HttpStatusCode.Created, """{"client_id":"dynamic-client","client_secret":"dyn-secret"}""");
        return Json(HttpStatusCode.NotFound, "{}");
    });

    [Fact]
    public async Task StartAuthAsync_WhenEndpointsMissing_ShouldDiscoverAndRegisterClient()
    {
        var handler = StandardHandler();
        var server = ServerWith(new McpOAuthConfig { Scope = "read" });
        var provider = CreateProvider(handler, CreateStorage(), server, CreateRegistrationStore());

        var result = await provider.StartAuthAsync("server-1");

        result.AuthorizationUrl.Should().StartWith("https://auth.example.com/authorize?")
            .And.Contain("client_id=dynamic-client")
            .And.Contain("scope=read");

        server.OAuth!.AuthorizationEndpoint.Should().Be("https://auth.example.com/authorize");
        server.OAuth.TokenEndpoint.Should().Be("https://auth.example.com/token");

        handler.CountFor("oauth-protected-resource", HttpMethod.Get).Should().Be(1);
        handler.CountFor("oauth-authorization-server", HttpMethod.Get).Should().Be(1);
        handler.CountFor("/register", HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task StartAuthAsync_WhenEndpointsExplicit_ShouldNotDiscover()
    {
        var handler = StandardHandler();
        var server = ServerWith(new McpOAuthConfig
        {
            ClientId = "client-1",
            AuthorizationEndpoint = "https://explicit.example.com/authorize",
            TokenEndpoint = "https://explicit.example.com/token"
        });
        var provider = CreateProvider(handler, CreateStorage(), server, CreateRegistrationStore());

        var result = await provider.StartAuthAsync("server-1");

        result.AuthorizationUrl.Should().StartWith("https://explicit.example.com/authorize?")
            .And.Contain("client_id=client-1");
        handler.Requests.Should().BeEmpty("显式配置齐备时不应发起发现/注册请求");
    }

    [Fact]
    public async Task StartAuthAsync_WhenExplicitEndpointPartiallyConfigured_ShouldFillOnlyMissing()
    {
        var handler = StandardHandler();
        var server = ServerWith(new McpOAuthConfig
        {
            ClientId = "client-1",
            AuthorizationEndpoint = "https://explicit.example.com/authorize"
        });
        var provider = CreateProvider(handler, CreateStorage(), server, CreateRegistrationStore());

        var result = await provider.StartAuthAsync("server-1");

        result.AuthorizationUrl.Should().StartWith("https://explicit.example.com/authorize?");
        server.OAuth!.AuthorizationEndpoint.Should().Be("https://explicit.example.com/authorize");
        server.OAuth.TokenEndpoint.Should().Be("https://auth.example.com/token");
    }

    [Fact]
    public async Task StartAuthAsync_WhenRegistrationCached_ShouldNotRegisterAgain()
    {
        var registrationStore = CreateRegistrationStore();

        // 第一次：注册并缓存
        var firstServer = ServerWith(new McpOAuthConfig());
        var firstHandler = StandardHandler();
        var firstProvider = CreateProvider(firstHandler, CreateStorage(), firstServer, registrationStore);
        await firstProvider.StartAuthAsync("server-1");
        firstHandler.CountFor("/register", HttpMethod.Post).Should().Be(1);

        // 第二次：新实例/新 handler，应从缓存读取 client_id，不再 POST 注册
        var secondServer = ServerWith(new McpOAuthConfig());
        var secondHandler = StandardHandler();
        var secondProvider = CreateProvider(secondHandler, CreateStorage(), secondServer, registrationStore);

        var result = await secondProvider.StartAuthAsync("server-1");

        result.AuthorizationUrl.Should().Contain("client_id=dynamic-client");
        secondHandler.CountFor("/register", HttpMethod.Post).Should().Be(0);
        secondServer.OAuth!.ClientId.Should().Be("dynamic-client");
    }

    [Fact]
    public async Task StartAuthAsync_WhenDiscoveryFails_ShouldFallBackToExplicitError()
    {
        var handler = new RoutingHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        var server = ServerWith(new McpOAuthConfig { ClientId = "client-1" });
        var provider = CreateProvider(handler, CreateStorage(), server, CreateRegistrationStore());

        var act = () => provider.StartAuthAsync("server-1");

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*授权端点*");
    }

    [Fact]
    public void BuildWellKnownUrl_ShouldInsertWellKnownBeforeResourcePath()
    {
        McpOAuthDiscovery.BuildWellKnownUrl(
                new Uri("https://mcp.example.com/mcp"), "oauth-protected-resource")
            .ToString().Should().Be("https://mcp.example.com/.well-known/oauth-protected-resource/mcp");

        McpOAuthDiscovery.BuildWellKnownUrl(
                new Uri("https://auth.example.com"), "oauth-authorization-server")
            .ToString().Should().Be("https://auth.example.com/.well-known/oauth-authorization-server");
    }

    private sealed class FakeCallbackServer : IMcpOAuthCallbackServer
    {
        public Task<int> EnsureRunningAsync() => Task.FromResult(59125);

        public string GetCallbackUrl() => "http://localhost:59125/callback";

        public Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout)
            => Task.FromResult(("code", state));
    }

    /// <summary>按请求分发响应并记录全部请求（方法/URL/正文）。</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = new();

        public int CountFor(string urlFragment, HttpMethod method)
            => Requests.Count(r => r.Method == method
                && r.Uri.ToString().Contains(urlFragment, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add((request.Method, request.RequestUri!, body));
            return _responder(request);
        }
    }
}
