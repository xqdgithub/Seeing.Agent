using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// MCP OAuth 授权编排实现：串联 <see cref="IMcpOAuthProvider.StartAuthAsync"/>、
    /// <see cref="IBrowserLauncher"/> 与 <see cref="IMcpOAuthCallbackServer.WaitForCallbackAsync"/>，
    /// 最后交由 <see cref="IMcpOAuthProvider.FinishAuthAsync"/> 交换并持久化令牌。
    /// </summary>
    public sealed class McpOAuthAuthorizer : IMcpOAuthAuthorizer
    {
        /// <summary>默认等待授权回调的超时时间。</summary>
        public static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromMinutes(5);

        private readonly IMcpOAuthProvider _provider;
        private readonly IMcpOAuthCallbackServer _callbackServer;
        private readonly IBrowserLauncher _browserLauncher;
        private readonly ILogger<McpOAuthAuthorizer> _logger;
        private readonly TimeSpan _callbackTimeout;

        public McpOAuthAuthorizer(
            IMcpOAuthProvider provider,
            IMcpOAuthCallbackServer callbackServer,
            IBrowserLauncher browserLauncher,
            ILogger<McpOAuthAuthorizer> logger,
            TimeSpan? callbackTimeout = null)
        {
            _provider = provider;
            _callbackServer = callbackServer;
            _browserLauncher = browserLauncher;
            _logger = logger;
            _callbackTimeout = callbackTimeout ?? DefaultCallbackTimeout;
        }

        /// <inheritdoc />
        public async Task<OAuthResult> AuthorizeAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            OAuthStartResult start;
            try
            {
                start = await _provider.StartAuthAsync(mcpName, cancellationToken).ConfigureAwait(false);
            }
            catch (McpOAuthException ex)
            {
                _logger.LogWarning(ex, "启动 MCP OAuth 授权失败: {Server}", mcpName);
                return new OAuthResult(false, ex.Status ?? McpAuthStatus.NeedsAuthorization, ex.Message);
            }

            // 无宿主交互能力时不等待回调，直接提示手动打开（不伪造令牌）
            if (!_browserLauncher.TryOpen(start.AuthorizationUrl))
            {
                _logger.LogWarning("无法自动打开浏览器，需用户手动完成 MCP OAuth 授权: {Server}", mcpName);
                return new OAuthResult(
                    false,
                    McpAuthStatus.NeedsAuthorization,
                    $"无法自动打开浏览器，请手动访问以下 URL 完成授权：{start.AuthorizationUrl}");
            }

            (string Code, string State) callback;
            try
            {
                callback = await _callbackServer
                    .WaitForCallbackAsync(start.State, _callbackTimeout)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("等待 MCP OAuth 授权回调超时: {Server}", mcpName);
                return new OAuthResult(
                    false,
                    McpAuthStatus.NeedsAuthorization,
                    $"等待授权回调超时（{_callbackTimeout.TotalSeconds:0} 秒），请重试");
            }

            return await _provider
                .FinishAuthAsync(mcpName, callback.Code, callback.State, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
