using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<IMcpOAuthProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<McpOAuthProvider>>();
            var storage = sp.GetRequiredService<McpOAuthStorage>();
            var callbackServer = sp.GetRequiredService<IMcpOAuthCallbackServer>();
            var tokenClient = sp.GetRequiredService<McpOAuthTokenClient>();

            // IMcpManager 可能尚未注册（仅需授权流程时），缺失则返回 null 由 Provider 显式报错
            var manager = sp.GetService<IMcpManager>();

            return new McpOAuthProvider(
                logger,
                storage,
                callbackServer,
                tokenClient,
                name => manager?.GetConfig(name)?.OAuth);
        });

        return services;
    }
}
