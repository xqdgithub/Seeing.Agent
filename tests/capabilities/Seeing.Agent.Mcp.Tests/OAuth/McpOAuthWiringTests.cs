using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.OAuth;

/// <summary>
/// OAuth 服务注册与配置解析接线：AddMcpOAuth 将 Provider/存储/令牌客户端/授权器/浏览器打开器
/// 注入 DI；Provider 的配置解析经 IMcpManager.GetConfig 动态读取（模块激活后配置可用）。
/// </summary>
public class McpOAuthWiringTests
{
    [Fact]
    public void AddMcpOAuth_ShouldRegisterCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddMcpOAuth();

        using var provider = services.BuildServiceProvider();

        provider.GetService<McpOAuthStorage>().Should().NotBeNull();
        provider.GetService<McpOAuthTokenClient>().Should().NotBeNull();
        provider.GetService<McpOAuthDiscovery>().Should().NotBeNull();
        provider.GetService<McpOAuthClientRegistrar>().Should().NotBeNull();
        provider.GetService<IBrowserLauncher>().Should().NotBeNull();
        provider.GetService<IMcpOAuthProvider>().Should().NotBeNull();
        provider.GetService<IMcpOAuthAuthorizer>().Should().NotBeNull();
        provider.GetService<IMcpOAuthConnectionPreparer>().Should().NotBeNull();
    }

    [Fact]
    public async Task Provider_ConfigResolver_ShouldReadFromMcpManagerGetConfig()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetConfig("server-1")).Returns(new McpServerConfig
        {
            Name = "server-1",
            TransportType = McpTransportType.StreamableHttp,
            Url = new Uri("https://mcp.example.com/mcp"),
            OAuth = new McpOAuthConfig
            {
                ClientId = "client-1",
                AuthorizationEndpoint = "https://auth.example.com/authorize",
                TokenEndpoint = "https://auth.example.com/token"
            }
        });

        var provider = BuildProviderWithManager(manager.Object);

        var start = await provider.StartAuthAsync("server-1");

        start.AuthorizationUrl.Should().StartWith("https://auth.example.com/authorize?")
            .And.Contain("client_id=client-1");
        manager.Verify(m => m.GetConfig("server-1"), Times.Once);
    }

    [Fact]
    public async Task Provider_ConfigResolver_WhenManagerHasNoConfig_ShouldThrowExplicitError()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetConfig(It.IsAny<string>())).Returns((McpServerConfig?)null);

        var provider = BuildProviderWithManager(manager.Object);

        var act = () => provider.StartAuthAsync("unknown-server");

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*未配置 OAuth*");
    }

    private static IMcpOAuthProvider BuildProviderWithManager(IMcpManager manager)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(manager);
        services.AddMcpOAuth();
        services.AddSingleton<IMcpOAuthCallbackServer>(new StubCallbackServer());

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMcpOAuthProvider>();
    }

    private sealed class StubCallbackServer : IMcpOAuthCallbackServer
    {
        public Task<int> EnsureRunningAsync() => Task.FromResult(59124);

        public string GetCallbackUrl() => "http://localhost:59124/callback";

        public Task<(string Code, string State)> WaitForCallbackAsync(string state, TimeSpan timeout)
            => Task.FromResult(("code", state));
    }
}
