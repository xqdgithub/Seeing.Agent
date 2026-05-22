using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.MCP.OAuth;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// MCP OAuth 服务扩展
    /// </summary>
    public static class McpOAuthServiceExtensions
    {
        /// <summary>
        /// 添加 MCP OAuth 支持
        /// </summary>
        public static IServiceCollection AddMcpOAuth(this IServiceCollection services)
        {
            services.AddSingleton<McpOAuthStorage>();
            services.AddSingleton<McpOAuthCallbackServer>();
            services.AddSingleton<IMcpOAuthProvider, McpOAuthProvider>();
            
            return services;
        }
    }
}
