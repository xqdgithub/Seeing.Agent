using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

public class OAuthHttpClientWrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "seeing-mcp-oauth-wrapper-tests", Guid.NewGuid().ToString("N"));

    public OAuthHttpClientWrapperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    private McpOAuthStorage CreateStorage()
        => new(NullLogger<McpOAuthStorage>.Instance,
            storageDirectory: Path.Combine(_root, "tokens"),
            keyFilePath: Path.Combine(_root, "key.bin"));

    private static McpServerConfig CreateConfig(McpOAuthConfig? oauth) => new()
    {
        Name = "server-1",
        TransportType = McpTransportType.StreamableHttp,
        Url = new Uri("https://mcp.example.com/mcp"),
        OAuth = oauth
    };

    private static OAuthHttpClientWrapper CreateWrapper(
        McpServerConfig config,
        McpOAuthStorage storage,
        RecordingHandler handler)
    {
        var tokenClient = new McpOAuthTokenClient(
            NullLogger<McpOAuthTokenClient>.Instance,
            new FakeHttpClientFactory(handler));
        return new OAuthHttpClientWrapper(
            config,
            new FakeHttpClientFactory(handler),
            NullLogger.Instance,
            HttpTransportMode.StreamableHttp,
            storage,
            tokenClient);
    }

    [Fact]
    public async Task ResolveEffectiveConfig_WithStoredToken_ShouldInjectBearerHeader()
    {
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken
        {
            AccessToken = "at-valid",
            TokenType = "Bearer",
            ExpiresIn = 3600
        });
        var config = CreateConfig(new McpOAuthConfig { TokenEndpoint = "https://auth.example.com/token" });
        var wrapper = CreateWrapper(config, storage, RecordingHandler.Json(HttpStatusCode.OK, "{}"));

        var effective = await wrapper.ResolveEffectiveConfigAsync(CancellationToken.None);

        effective.Headers.Should().ContainKey("Authorization");
        effective.Headers!["Authorization"].Should().Be("Bearer at-valid");
        // 原始配置不应被修改
        config.Headers.Should().BeNull();
    }

    [Fact]
    public async Task ResolveEffectiveConfig_WithoutStoredToken_ShouldNotInjectHeader()
    {
        var config = CreateConfig(new McpOAuthConfig { TokenEndpoint = "https://auth.example.com/token" });
        var wrapper = CreateWrapper(config, CreateStorage(), RecordingHandler.Json(HttpStatusCode.OK, "{}"));

        var effective = await wrapper.ResolveEffectiveConfigAsync(CancellationToken.None);

        (effective.Headers is null || !effective.Headers.ContainsKey("Authorization")).Should().BeTrue();
    }

    [Fact]
    public async Task ResolveEffectiveConfig_WhenTokenExpiringSoon_ShouldRefreshBeforeInjecting()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK,
            """{"access_token":"at-refreshed","refresh_token":"rt-2","token_type":"Bearer","expires_in":3600}""");
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken
        {
            AccessToken = "at-stale",
            RefreshToken = "rt-1",
            ExpiresIn = 60 // 5 分钟内视为即将过期
        });
        var config = CreateConfig(new McpOAuthConfig
        {
            ClientId = "client-1",
            TokenEndpoint = "https://auth.example.com/token"
        });
        var wrapper = CreateWrapper(config, storage, handler);

        var effective = await wrapper.ResolveEffectiveConfigAsync(CancellationToken.None);

        effective.Headers!["Authorization"].Should().Be("Bearer at-refreshed");
        handler.CallCount.Should().Be(1);
        (await storage.LoadTokenAsync("server-1"))!.AccessToken.Should().Be("at-refreshed");
    }

    [Fact]
    public async Task ResolveEffectiveConfig_WhenOAuthDisabled_ShouldLeaveConfigUnchanged()
    {
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken { AccessToken = "at", ExpiresIn = 3600 });
        var config = CreateConfig(new McpOAuthConfig { Disabled = true });
        var wrapper = CreateWrapper(config, storage, RecordingHandler.Json(HttpStatusCode.OK, "{}"));

        var effective = await wrapper.ResolveEffectiveConfigAsync(CancellationToken.None);

        (effective.Headers is null || !effective.Headers.ContainsKey("Authorization")).Should().BeTrue();
    }

    [Fact]
    public async Task ResolveEffectiveConfig_WhenExpiredWithoutRefreshToken_ShouldNotInjectHeader()
    {
        var storage = CreateStorage();
        await storage.SaveTokenAsync("server-1", new McpOAuthToken
        {
            AccessToken = "at-expired",
            ExpiresIn = -10
        });
        var config = CreateConfig(new McpOAuthConfig { TokenEndpoint = "https://auth.example.com/token" });
        var wrapper = CreateWrapper(config, storage, RecordingHandler.Json(HttpStatusCode.OK, "{}"));

        var effective = await wrapper.ResolveEffectiveConfigAsync(CancellationToken.None);

        (effective.Headers is null || !effective.Headers.ContainsKey("Authorization")).Should().BeTrue();
    }
}
