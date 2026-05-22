using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// MCP OAuth 提供者实现 - PKCE 流程
    /// </summary>
    public class McpOAuthProvider : IMcpOAuthProvider
    {
        private readonly ILogger<McpOAuthProvider> _logger;
        private readonly McpOAuthStorage _storage;
        private readonly McpOAuthCallbackServer _callbackServer;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ConcurrentDictionary<string, PendingAuth> _pendingAuths = new();

        public McpOAuthProvider(
            ILogger<McpOAuthProvider> logger,
            McpOAuthStorage storage,
            McpOAuthCallbackServer callbackServer,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _storage = storage;
            _callbackServer = callbackServer;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<OAuthStartResult> StartAuthAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting OAuth flow for MCP server: {McpName}", mcpName);

            var codeVerifier = GenerateCodeVerifier();
            var codeChallenge = ComputeS256Challenge(codeVerifier);
            var state = Guid.NewGuid().ToString("N");

            var port = await _callbackServer.EnsureRunningAsync();
            var redirectUri = _callbackServer.GetCallbackUrl();

            _pendingAuths[mcpName] = new PendingAuth(state, codeVerifier, redirectUri);

            // 构建授权 URL（占位 - 实际需要 OAuth 配置中的端点）
            var authorizationUrl = $"https://example.com/oauth/authorize?" +
                $"response_type=code&" +
                $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
                $"state={state}&" +
                $"code_challenge={codeChallenge}&" +
                $"code_challenge_method=S256";

            return new OAuthStartResult(authorizationUrl, state, port, codeVerifier);
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

            if (pending.State != state)
            {
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, "State mismatch - possible CSRF attack");
            }

            try
            {
                // TODO: 使用 pending.RedirectUri 和 authorizationCode 交换令牌
                var token = new McpOAuthToken
                {
                    AccessToken = "placeholder",
                    ExpiresIn = 3600
                };

                await _storage.SaveTokenAsync(mcpName, token);
                return new OAuthResult(true, McpAuthStatus.Authenticated, Token: token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to exchange token for {McpName}", mcpName);
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, ex.Message);
            }
        }

        public async Task<OAuthResult> AuthenticateAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            var token = await _storage.LoadTokenAsync(mcpName);
            
            if (token == null)
                return new OAuthResult(false, McpAuthStatus.NotAuthenticated, "No stored token found");

            if (token.IsExpired)
            {
                if (!string.IsNullOrEmpty(token.RefreshToken))
                    return await RefreshTokenAsync(mcpName, cancellationToken);
                return new OAuthResult(false, McpAuthStatus.Expired, "Token expired");
            }

            return new OAuthResult(true, McpAuthStatus.Authenticated, Token: token);
        }

        public async Task<OAuthResult> RefreshTokenAsync(
            string mcpName,
            CancellationToken cancellationToken = default)
        {
            var token = await _storage.LoadTokenAsync(mcpName);
            
            if (token == null || string.IsNullOrEmpty(token.RefreshToken))
                return new OAuthResult(false, McpAuthStatus.NeedsAuthorization, "No refresh token available");

            // TODO: 实现令牌刷新 - POST token_endpoint
            _logger.LogInformation("Refreshing token for {McpName}", mcpName);
            return new OAuthResult(false, McpAuthStatus.NeedsAuthorization, "Token refresh not fully implemented");
        }

        public async Task RemoveAuthAsync(string mcpName)
        {
            await _storage.DeleteTokenAsync(mcpName);
            _pendingAuths.TryRemove(mcpName, out _);
            _logger.LogInformation("Removed OAuth for {McpName}", mcpName);
        }

        public async Task<bool> HasStoredTokensAsync(string mcpName)
        {
            return await _storage.TokenExistsAsync(mcpName);
        }

        public async Task<McpAuthStatus> GetAuthStatusAsync(string mcpName)
        {
            var token = await _storage.LoadTokenAsync(mcpName);
            
            if (token == null) return McpAuthStatus.NotAuthenticated;
            if (token.IsExpired) return McpAuthStatus.Expired;
            return McpAuthStatus.Authenticated;
        }

        private static string GenerateCodeVerifier()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncode(bytes);
        }

        private static string ComputeS256Challenge(string codeVerifier)
        {
            var bytes = Encoding.UTF8.GetBytes(codeVerifier);
            var hash = SHA256.HashData(bytes);
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private record PendingAuth(string State, string CodeVerifier, string RedirectUri);
    }
}
