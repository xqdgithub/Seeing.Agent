using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp.OAuth;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 令牌端点客户端：授权码交换与 refresh_token 刷新（RFC 6749 / MCP 授权规范）。
    /// <para>仅使用表单 POST + JSON 响应，不实现动态客户端注册与元数据发现（由显式配置提供端点）。</para>
    /// </summary>
    public sealed class McpOAuthTokenClient
    {
        private const string HttpClientName = "McpOAuth";

        private readonly ILogger<McpOAuthTokenClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public McpOAuthTokenClient(
            ILogger<McpOAuthTokenClient> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>用授权码交换访问令牌（可选附带 PKCE code_verifier）。</summary>
        public async Task<McpOAuthToken> ExchangeCodeAsync(
            McpOAuthConfig config,
            string authorizationCode,
            string redirectUri,
            string? codeVerifier,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(authorizationCode);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                ["redirect_uri"] = redirectUri
            };

            AddClientCredentials(form, config);

            if (!string.IsNullOrEmpty(codeVerifier))
                form["code_verifier"] = codeVerifier;

            return await PostTokenAsync(RequireTokenEndpoint(config), form, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>用 refresh_token 刷新访问令牌。</summary>
        public async Task<McpOAuthToken> RefreshAsync(
            McpOAuthConfig config,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(refreshToken);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            };

            AddClientCredentials(form, config);

            var token = await PostTokenAsync(RequireTokenEndpoint(config), form, cancellationToken)
                .ConfigureAwait(false);

            // 刷新响应可能不返回新的 refresh_token，此时沿用旧值
            if (string.IsNullOrEmpty(token.RefreshToken))
                token.RefreshToken = refreshToken;

            return token;
        }

        private static void AddClientCredentials(Dictionary<string, string> form, McpOAuthConfig config)
        {
            if (!string.IsNullOrEmpty(config.ClientId))
                form["client_id"] = config.ClientId!;

            if (!string.IsNullOrEmpty(config.ClientSecret))
                form["client_secret"] = config.ClientSecret!;
        }

        private static string RequireTokenEndpoint(McpOAuthConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.TokenEndpoint))
            {
                throw new McpOAuthException(
                    "未配置 OAuth Token 端点（McpOAuthConfig.TokenEndpoint），无法完成令牌交换");
            }

            return config.TokenEndpoint!;
        }

        private async Task<McpOAuthToken> PostTokenAsync(
            string endpoint,
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpOAuthException($"OAuth Token 请求失败: {ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "OAuth Token 端点返回 {StatusCode}: {Body}",
                        (int)response.StatusCode,
                        Truncate(body));
                    throw new McpOAuthException(
                        $"OAuth Token 端点返回 {(int)response.StatusCode}: {Truncate(body)}");
                }

                return ParseToken(body);
            }
        }

        private static McpOAuthToken ParseToken(string body)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new McpOAuthException($"OAuth Token 响应不是有效 JSON: {ex.Message}", ex);
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(accessToken.GetString()))
            {
                throw new McpOAuthException("OAuth Token 响应缺少 access_token 字段");
            }

            var token = new McpOAuthToken
            {
                AccessToken = accessToken.GetString()!,
                CreatedAt = DateTimeOffset.Now
            };

            if (root.TryGetProperty("refresh_token", out var refreshToken) &&
                refreshToken.ValueKind == JsonValueKind.String)
            {
                token.RefreshToken = refreshToken.GetString();
            }

            if (root.TryGetProperty("token_type", out var tokenType) &&
                tokenType.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(tokenType.GetString()))
            {
                token.TokenType = tokenType.GetString()!;
            }

            if (root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
                token.Scope = scope.GetString();

            token.ExpiresIn = ReadExpiresIn(root);

            return token;
        }

        private static int ReadExpiresIn(JsonElement root)
        {
            if (!root.TryGetProperty("expires_in", out var expiresIn))
                return 0;

            if (expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt32(out var seconds))
                return seconds;

            if (expiresIn.ValueKind == JsonValueKind.String &&
                int.TryParse(expiresIn.GetString(), out var parsed))
            {
                return parsed;
            }

            return 0;
        }

        private static string Truncate(string value)
            => value.Length <= 512 ? value : value[..512];
    }
}
