using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// MCP OAuth 提供者实现 - OAuth 2.1 授权码 + PKCE 流程。
    /// <para>
    /// 端点来自 server 的 <see cref="McpOAuthConfig"/> 显式配置（不实现元数据自动发现）。
    /// 若未配置授权/令牌端点，或运行环境无宿主交互能力，则显式失败而非伪造令牌。
    /// </para>
    /// </summary>
    public class McpOAuthProvider : IMcpOAuthProvider
    {
        private readonly ILogger<McpOAuthProvider> _logger;
        private readonly McpOAuthStorage _storage;
        private readonly IMcpOAuthCallbackServer _callbackServer;
        private readonly McpOAuthTokenClient _tokenClient;
        private readonly Func<string, McpOAuthConfig?> _configResolver;
        private readonly ConcurrentDictionary<string, PendingAuth> _pendingAuths = new();

        public McpOAuthProvider(
            ILogger<McpOAuthProvider> logger,
            McpOAuthStorage storage,
            IMcpOAuthCallbackServer callbackServer,
            McpOAuthTokenClient tokenClient,
            Func<string, McpOAuthConfig?> configResolver)
        {
            _logger = logger;
            _storage = storage;
            _callbackServer = callbackServer;
            _tokenClient = tokenClient;
            _configResolver = configResolver;
        }

        public async Task<OAuthStartResult> StartAuthAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            var config = RequireEnabledConfig(mcpName);

            if (string.IsNullOrWhiteSpace(config.AuthorizationEndpoint))
            {
                throw new McpOAuthException(
                    $"MCP Server {mcpName} 未配置 OAuth 授权端点（McpOAuthConfig.AuthorizationEndpoint）",
                    mcpName,
                    McpAuthStatus.NeedsAuthorization);
            }

            _logger.LogInformation("Starting OAuth flow for MCP server: {McpName}", mcpName);

            var usePkce = config.UsePkce;
            var codeVerifier = usePkce ? McpOAuthPkce.CreateCodeVerifier() : null;
            var codeChallenge = codeVerifier is null ? null : McpOAuthPkce.CreateCodeChallenge(codeVerifier);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            var port = await _callbackServer.EnsureRunningAsync().ConfigureAwait(false);
            var redirectUri = string.IsNullOrWhiteSpace(config.RedirectUri)
                ? _callbackServer.GetCallbackUrl()
                : config.RedirectUri!;

            _pendingAuths[mcpName] = new PendingAuth(state, codeVerifier ?? "", redirectUri, config);

            var authorizationUrl = BuildAuthorizationUrl(config, redirectUri, state, codeChallenge);

            return new OAuthStartResult(authorizationUrl, state, port, codeVerifier ?? "");
        }

        public async Task<OAuthResult> FinishAuthAsync(
            string mcpName,
            string authorizationCode,
            string state,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Finishing OAuth for MCP server: {McpName}", mcpName);

            if (!_pendingAuths.TryRemove(mcpName, out var pending))
            {
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, "No pending authorization found");
            }

            if (!StatesMatch(pending.State, state))
            {
                _logger.LogWarning("OAuth state 校验失败，拒绝授权回调: {McpName}", mcpName);
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, "State mismatch - possible CSRF attack");
            }

            try
            {
                var token = await _tokenClient.ExchangeCodeAsync(
                    pending.Config,
                    authorizationCode,
                    pending.RedirectUri,
                    pending.CodeVerifier,
                    cancellationToken).ConfigureAwait(false);

                await _storage.SaveTokenAsync(mcpName, token).ConfigureAwait(false);
                return new OAuthResult(true, McpAuthStatus.Authenticated, Token: token);
            }
            catch (McpOAuthException ex)
            {
                _logger.LogError(ex, "Failed to exchange token for {McpName}", mcpName);
                return new OAuthResult(false, McpAuthStatus.NeedsAuthorization, ex.Message);
            }
        }

        public async Task<OAuthResult> AuthenticateAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            var token = await _storage.LoadTokenAsync(mcpName).ConfigureAwait(false);

            if (token == null)
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, "No stored token found");

            if (token.IsExpired)
            {
                if (!string.IsNullOrEmpty(token.RefreshToken))
                    return await RefreshTokenAsync(mcpName, cancellationToken).ConfigureAwait(false);

                return new OAuthResult(false, McpAuthStatus.Expired, "Token expired");
            }

            // 过期前主动刷新
            if (token.IsExpiringSoon && !string.IsNullOrEmpty(token.RefreshToken))
                return await RefreshTokenAsync(mcpName, cancellationToken).ConfigureAwait(false);

            return new OAuthResult(true, McpAuthStatus.Authenticated, Token: token);
        }

        public async Task<OAuthResult> RefreshTokenAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            var token = await _storage.LoadTokenAsync(mcpName).ConfigureAwait(false);

            if (token == null || string.IsNullOrEmpty(token.RefreshToken))
                return new OAuthResult(false, McpAuthStatus.NeedsAuthorization, "No refresh token available");

            var config = _configResolver(mcpName);
            if (config == null || config.Disabled)
            {
                return new OAuthResult(false, McpAuthStatus.NeedsAuthorization,
                    $"MCP Server {mcpName} 未配置 OAuth，无法刷新令牌");
            }

            try
            {
                var refreshed = await _tokenClient.RefreshAsync(config, token.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                await _storage.SaveTokenAsync(mcpName, refreshed).ConfigureAwait(false);
                return new OAuthResult(true, McpAuthStatus.Authenticated, Token: refreshed);
            }
            catch (McpOAuthException ex)
            {
                _logger.LogError(ex, "刷新 OAuth 令牌失败: {McpName}", mcpName);
                return new OAuthResult(false, McpAuthStatus.NeedsAuthorization, ex.Message);
            }
        }

        public async Task RemoveAuthAsync(string mcpName)
        {
            await _storage.DeleteTokenAsync(mcpName).ConfigureAwait(false);
            _pendingAuths.TryRemove(mcpName, out _);
            _logger.LogInformation("Removed OAuth for {McpName}", mcpName);
        }

        public async Task<bool> HasStoredTokensAsync(string mcpName)
        {
            return await _storage.TokenExistsAsync(mcpName).ConfigureAwait(false);
        }

        public async Task<McpAuthStatus> GetAuthStatusAsync(string mcpName)
        {
            var token = await _storage.LoadTokenAsync(mcpName).ConfigureAwait(false);

            if (token == null) return McpAuthStatus.NotAuthenticated;
            if (token.IsExpired) return McpAuthStatus.Expired;
            return McpAuthStatus.Authenticated;
        }

        private McpOAuthConfig RequireEnabledConfig(string mcpName)
        {
            var config = _configResolver(mcpName);
            if (config == null)
            {
                throw new McpOAuthException(
                    $"MCP Server {mcpName} 未配置 OAuth（McpOAuthConfig），无法启动授权",
                    mcpName,
                    McpAuthStatus.NeedsAuthorization);
            }

            if (config.Disabled)
            {
                throw new McpOAuthException(
                    $"MCP Server {mcpName} 的 OAuth 已被禁用",
                    mcpName,
                    McpAuthStatus.NeedsAuthorization);
            }

            return config;
        }

        private static string BuildAuthorizationUrl(
            McpOAuthConfig config,
            string redirectUri,
            string state,
            string? codeChallenge)
        {
            var parameters = new List<string>
            {
                "response_type=code",
                $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
                $"state={Uri.EscapeDataString(state)}"
            };

            if (!string.IsNullOrEmpty(config.ClientId))
                parameters.Add($"client_id={Uri.EscapeDataString(config.ClientId!)}");

            if (!string.IsNullOrEmpty(config.Scope))
                parameters.Add($"scope={Uri.EscapeDataString(config.Scope!)}");

            if (!string.IsNullOrEmpty(codeChallenge))
            {
                parameters.Add($"code_challenge={codeChallenge}");
                parameters.Add("code_challenge_method=S256");
            }

            var endpoint = config.AuthorizationEndpoint!;
            var separator = endpoint.Contains('?') ? "&" : "?";
            return $"{endpoint}{separator}{string.Join("&", parameters)}";
        }

        private static bool StatesMatch(string expected, string actual)
        {
            if (string.IsNullOrEmpty(actual)) return false;

            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private record PendingAuth(string State, string CodeVerifier, string RedirectUri, McpOAuthConfig Config);
    }
}
