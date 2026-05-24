using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// OAuth 令牌存储 - 使用 DPAPI 加密
    /// </summary>
    public class McpOAuthStorage
    {
        private readonly ILogger<McpOAuthStorage> _logger;
        private readonly string _storagePath;

        public McpOAuthStorage(ILogger<McpOAuthStorage> logger)
        {
            _logger = logger;
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "oauth-tokens");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>保存令牌（DPAPI 加密）</summary>
        public async Task SaveTokenAsync(string mcpName, McpOAuthToken token)
        {
            var filePath = GetTokenPath(mcpName);
            var json = JsonSerializer.Serialize(token);
            var bytes = Encoding.UTF8.GetBytes(json);

            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(filePath, encrypted);
            _logger.LogDebug("Saved OAuth token for {McpName}", mcpName);
        }

        /// <summary>加载令牌（DPAPI 解密）</summary>
        public async Task<McpOAuthToken?> LoadTokenAsync(string mcpName)
        {
            var filePath = GetTokenPath(mcpName);
            if (!File.Exists(filePath)) return null;

            try
            {
                var encrypted = await File.ReadAllBytesAsync(filePath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<McpOAuthToken>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load OAuth token for {McpName}", mcpName);
                return null;
            }
        }

        /// <summary>删除令牌</summary>
        public Task DeleteTokenAsync(string mcpName)
        {
            var filePath = GetTokenPath(mcpName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogDebug("Deleted OAuth token for {McpName}", mcpName);
            }
            return Task.CompletedTask;
        }

        /// <summary>检查令牌是否存在</summary>
        public Task<bool> TokenExistsAsync(string mcpName)
        {
            var filePath = GetTokenPath(mcpName);
            return Task.FromResult(File.Exists(filePath));
        }

        private string GetTokenPath(string mcpName)
        {
            var safeName = string.Join("_", mcpName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storagePath, $"{safeName}.token");
        }
    }
}
