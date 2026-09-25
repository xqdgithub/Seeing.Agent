using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// RFC 7591 动态客户端注册客户端。
    /// <para>
    /// 向授权服务器 <c>registration_endpoint</c> POST 客户端元数据，返回并缓存
    /// <c>client_id</c>（以及可选的 <c>client_secret</c>）。仅做最小可用实现，不处理
    /// registration_access_token / 客户端配置端点等扩展。
    /// </para>
    /// </summary>
    public sealed class McpOAuthClientRegistrar
    {
        private const string HttpClientName = "McpOAuth";

        private readonly ILogger<McpOAuthClientRegistrar> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly McpOAuthClientRegistrationStore _store;

        public McpOAuthClientRegistrar(
            ILogger<McpOAuthClientRegistrar> logger,
            IHttpClientFactory httpClientFactory,
            McpOAuthClientRegistrationStore store)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _store = store;
        }

        /// <summary>读取已缓存的注册结果；未缓存时返回 null。</summary>
        public Task<OAuthClientRegistration?> TryLoadAsync(string serverName)
            => _store.LoadAsync(serverName);

        /// <summary>
        /// 发起动态客户端注册并缓存结果；失败时返回 null（由调用方回退显式配置或明确报错）。
        /// </summary>
        /// <param name="serverName">MCP 服务器名称（缓存键）</param>
        /// <param name="registrationEndpoint">授权服务器注册端点</param>
        /// <param name="redirectUri">回调地址（作为 redirect_uris）</param>
        /// <param name="scope">授权范围（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<OAuthClientRegistration?> RegisterAsync(
            string serverName,
            string registrationEndpoint,
            string redirectUri,
            string? scope = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(registrationEndpoint) || string.IsNullOrWhiteSpace(redirectUri))
                return null;

            var payload = new Dictionary<string, object?>
            {
                ["client_name"] = $"Seeing.Agent MCP ({serverName})",
                ["redirect_uris"] = new[] { redirectUri },
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                // PKCE 公共客户端约定：无密钥
                ["token_endpoint_auth_method"] = "none"
            };

            if (!string.IsNullOrWhiteSpace(scope))
                payload["scope"] = scope;

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
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
                _logger.LogWarning(ex, "OAuth 动态客户端注册请求失败: {Server}", serverName);
                return null;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "OAuth 动态客户端注册失败（{Status}）: {Server}",
                        (int)response.StatusCode, serverName);
                    return null;
                }

                var registration = ParseRegistration(body);
                if (registration is null)
                {
                    _logger.LogWarning("OAuth 动态客户端注册响应缺少 client_id: {Server}", serverName);
                    return null;
                }

                await _store.SaveAsync(serverName, registration).ConfigureAwait(false);
                return registration;
            }
        }

        private static OAuthClientRegistration? ParseRegistration(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("client_id", out var clientId) ||
                    clientId.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(clientId.GetString()))
                {
                    return null;
                }

                string? clientSecret = null;
                if (root.TryGetProperty("client_secret", out var secret) &&
                    secret.ValueKind == JsonValueKind.String)
                {
                    clientSecret = secret.GetString();
                }

                return new OAuthClientRegistration(clientId.GetString()!, clientSecret);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
