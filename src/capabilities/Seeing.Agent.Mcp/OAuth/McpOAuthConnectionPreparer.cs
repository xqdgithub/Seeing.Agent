using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// 连接时 OAuth 预处理实现：复用 <see cref="IMcpOAuthProvider.AuthenticateAsync"/>
    /// 校验/刷新令牌，必要时经 <see cref="IMcpOAuthAuthorizer"/> 触发授权闭环。
    /// </summary>
    public sealed class McpOAuthConnectionPreparer : IMcpOAuthConnectionPreparer
    {
        private readonly IMcpOAuthProvider _provider;
        private readonly IMcpOAuthAuthorizer _authorizer;
        private readonly ILogger<McpOAuthConnectionPreparer> _logger;

        public McpOAuthConnectionPreparer(
            IMcpOAuthProvider provider,
            IMcpOAuthAuthorizer authorizer,
            ILogger<McpOAuthConnectionPreparer> logger)
        {
            _provider = provider;
            _authorizer = authorizer;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<OAuthResult> EnsureAuthorizedAsync(
            string mcpName,
            McpServerConfig config,
            CancellationToken cancellationToken = default)
        {
            var oauth = config.OAuth;
            if (oauth is null || oauth.Disabled)
                return new OAuthResult(true, McpAuthStatus.Authenticated);

            var auth = await _provider.AuthenticateAsync(mcpName, cancellationToken).ConfigureAwait(false);
            if (auth.Success)
                return auth;

            if (oauth.AutoAuthorize)
            {
                _logger.LogInformation(
                    "MCP Server {Server} 无有效 OAuth 令牌，按配置自动触发授权", mcpName);
                return await _authorizer.AuthorizeAsync(mcpName, cancellationToken).ConfigureAwait(false);
            }

            // 非交互宿主/未启用自动授权：给出明确、可操作的提示，绝不阻塞
            _logger.LogInformation(
                "MCP Server {Server} 需要 OAuth 授权（未启用自动授权，跳过打开浏览器）", mcpName);
            return new OAuthResult(
                false,
                McpAuthStatus.NeedsAuthorization,
                $"MCP Server {mcpName} 需要 OAuth 授权，请运行 /mcp-auth {mcpName}");
        }
    }
}
