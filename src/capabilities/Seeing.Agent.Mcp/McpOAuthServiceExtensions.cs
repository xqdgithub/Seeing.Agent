using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP OAuth 服务扩展
/// </summary>
public static class McpOAuthServiceExtensions
{
    /// <summary>
    /// 添加 MCP OAuth 支持。
    /// <para>
    /// 令牌端点/授权端点取自各 MCP Server 的 <see cref="McpServerConfig.OAuth"/> 配置
    /// （经 <see cref="IMcpManager.GetConfig"/> 解析）；需要宿主提供 <c>IHttpClientFactory</c>。
    /// </para>
    /// </summary>
    public static IServiceCollection AddMcpOAuth(this IServiceCollection services)
    {
        services.AddSingleton<McpOAuthStorage>();
        services.AddSingleton<IMcpOAuthCallbackServer>(sp =>
            new McpOAuthCallbackServer(sp.GetRequiredService<ILogger<McpOAuthCallbackServer>>()));
        services.AddSingleton<McpOAuthTokenClient>();
        services.AddSingleton<McpOAuthDiscovery>();
        services.AddSingleton<McpOAuthClientRegistrationStore>();
        services.AddSingleton<McpOAuthClientRegistrar>();

        services.AddSingleton<IMcpOAuthProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<McpOAuthProvider>>();
            var storage = sp.GetRequiredService<McpOAuthStorage>();
            var callbackServer = sp.GetRequiredService<IMcpOAuthCallbackServer>();
            var tokenClient = sp.GetRequiredService<McpOAuthTokenClient>();
            var discovery = sp.GetRequiredService<McpOAuthDiscovery>();
            var registrar = sp.GetRequiredService<McpOAuthClientRegistrar>();

            // IMcpManager 延迟解析（避免与连接预处理形成构造期循环依赖）；缺失则返回 null 由 Provider 显式报错
            return new McpOAuthProvider(
                logger,
                storage,
                callbackServer,
                tokenClient,
                name => sp.GetService<IMcpManager>()?.GetConfig(name),
                discovery,
                registrar);
        });

        // 宿主可先行注册自定义浏览器打开器覆盖默认实现（TryAdd 不覆盖已有注册）
        services.TryAddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IMcpOAuthAuthorizer, McpOAuthAuthorizer>();
        services.AddSingleton<IMcpOAuthConnectionPreparer, McpOAuthConnectionPreparer>();

        return services;
    }
}
