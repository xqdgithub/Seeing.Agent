using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// 动态客户端注册结果缓存（按 server 维度持久化）。
    /// <para>
    /// 存储目录默认 <c>~/.seeing/oauth-registrations</c>，每服务器一个 JSON 文件；
    /// 用于避免每次授权都重新执行 RFC 7591 注册。缓存只存 client_id/client_secret，
    /// 读取失败时按未缓存处理。
    /// </para>
    /// </summary>
    public sealed class McpOAuthClientRegistrationStore
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ILogger<McpOAuthClientRegistrationStore> _logger;
        private readonly string _storageDirectory;

        public McpOAuthClientRegistrationStore(
            ILogger<McpOAuthClientRegistrationStore> logger,
            string? storageDirectory = null)
        {
            _logger = logger;
            _storageDirectory = storageDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "oauth-registrations");
        }

        /// <summary>读取缓存的注册结果；不存在或损坏时返回 null。</summary>
        public async Task<OAuthClientRegistration?> LoadAsync(string serverName)
        {
            var path = GetPath(serverName);
            if (!File.Exists(path)) return null;

            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                var registration = JsonSerializer.Deserialize<OAuthClientRegistration>(json, s_jsonOptions);
                if (registration is null || string.IsNullOrWhiteSpace(registration.ClientId))
                    return null;

                return registration;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取动态客户端注册缓存失败: {Server}", serverName);
                return null;
            }
        }

        /// <summary>保存注册结果（覆盖已有缓存）。</summary>
        public async Task SaveAsync(string serverName, OAuthClientRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            Directory.CreateDirectory(_storageDirectory);
            var json = JsonSerializer.Serialize(registration, s_jsonOptions);

            // 先写临时文件再原子替换，避免中断留下半截缓存
            var path = GetPath(serverName);
            var tmpPath = path + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
            File.Move(tmpPath, path, overwrite: true);

            _logger.LogDebug("已缓存动态客户端注册: {Server}", serverName);
        }

        private string GetPath(string serverName)
        {
            var safeName = string.Join("_", serverName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storageDirectory, $"{safeName}.json");
        }
    }
}
