using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// MCP OAuth 元数据发现（RFC 9728 / RFC 8414）。
    /// <para>
    /// 先请求受保护资源元数据获取授权服务器 issuer，再请求授权服务器元数据获取
    /// 授权/令牌/注册端点。发现失败（网络异常、非 2xx、JSON 无效、字段缺失）
    /// 一律返回 null 并记录告警，由调用方回退显式配置或明确报错。
    /// </para>
    /// </summary>
    public sealed class McpOAuthDiscovery
    {
        private const string HttpClientName = "McpOAuth";
        private const string ProtectedResourceWellKnown = "oauth-protected-resource";
        private const string AuthorizationServerWellKnown = "oauth-authorization-server";

        private readonly ILogger<McpOAuthDiscovery> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public McpOAuthDiscovery(
            ILogger<McpOAuthDiscovery> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// 按 RFC 9728 请求受保护资源元数据；失败时返回 null。
        /// </summary>
        /// <param name="resourceUrl">MCP Server 的资源 URL（即连接端点）。</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<OAuthProtectedResourceMetadata?> TryGetProtectedResourceMetadataAsync(
            Uri resourceUrl,
            CancellationToken cancellationToken = default)
        {
            var url = BuildWellKnownUrl(resourceUrl, ProtectedResourceWellKnown);
            var root = await TryGetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (root is null) return null;

            if (!root.Value.TryGetProperty("authorization_servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("受保护资源元数据缺少 authorization_servers: {Url}", url);
                return null;
            }

            var issuers = servers
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();

            if (issuers.Count == 0)
            {
                _logger.LogWarning("受保护资源元数据未提供有效的授权服务器: {Url}", url);
                return null;
            }

            return new OAuthProtectedResourceMetadata(issuers);
        }

        /// <summary>
        /// 按 RFC 8414 请求授权服务器元数据；失败时返回 null。
        /// </summary>
        /// <param name="issuer">授权服务器 issuer。</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<OAuthAuthorizationServerMetadata?> TryGetAuthorizationServerMetadataAsync(
            string issuer,
            CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
            {
                _logger.LogWarning("授权服务器 issuer 不是有效绝对 URL: {Issuer}", issuer);
                return null;
            }

            var url = BuildWellKnownUrl(issuerUri, AuthorizationServerWellKnown);
            var root = await TryGetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (root is null) return null;

            return new OAuthAuthorizationServerMetadata(
                ReadString(root.Value, "authorization_endpoint"),
                ReadString(root.Value, "token_endpoint"),
                ReadString(root.Value, "registration_endpoint"));
        }

        /// <summary>
        /// 便捷入口：从资源 URL 依次发现并返回授权服务器元数据；任一步失败返回 null。
        /// </summary>
        public async Task<OAuthAuthorizationServerMetadata?> DiscoverAsync(
            Uri resourceUrl,
            CancellationToken cancellationToken = default)
        {
            var resource = await TryGetProtectedResourceMetadataAsync(resourceUrl, cancellationToken)
                .ConfigureAwait(false);
            var issuer = resource?.AuthorizationServers.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(issuer))
                return null;

            return await TryGetAuthorizationServerMetadataAsync(issuer, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 按 RFC 9728/8414 形式构造 well-known URL：将 <c>/.well-known/{name}</c>
        /// 插入 authority 与资源路径之间。
        /// </summary>
        internal static Uri BuildWellKnownUrl(Uri resource, string wellKnownName)
        {
            var authority = resource.GetLeftPart(UriPartial.Authority);
            var path = resource.AbsolutePath.TrimEnd('/');
            if (path == "/") path = string.Empty;
            return new Uri($"{authority}/.well-known/{wellKnownName}{path}");
        }

        private async Task<JsonElement?> TryGetJsonAsync(Uri url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("OAuth 元数据发现未命中（{Status}）: {Url}",
                        (int)response.StatusCode, url);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                return doc.RootElement.Clone();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OAuth 元数据发现失败: {Url}", url);
                return null;
            }
        }

        private static string? ReadString(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                return null;

            var result = value.GetString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }
}
