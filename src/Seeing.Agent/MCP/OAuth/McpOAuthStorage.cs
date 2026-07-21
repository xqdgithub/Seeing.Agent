using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// OAuth 令牌存储 - 使用 AES 加密（跨平台）
    /// </summary>
    public class McpOAuthStorage
    {
        private readonly ILogger<McpOAuthStorage> _logger;
        private readonly string _storagePath;
        private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Seeing.Agent.MCP.OAuth.Salt");

        public McpOAuthStorage(ILogger<McpOAuthStorage> logger)
        {
            _logger = logger;
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "oauth-tokens");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>保存令牌（AES 加密）</summary>
        public async Task SaveTokenAsync(string mcpName, McpOAuthToken token)
        {
            var filePath = GetTokenPath(mcpName);
            var json = JsonSerializer.Serialize(token);
            var bytes = Encoding.UTF8.GetBytes(json);

            var encrypted = Protect(bytes);
            await File.WriteAllBytesAsync(filePath, encrypted);
            _logger.LogDebug("Saved OAuth token for {McpName}", mcpName);
        }

        /// <summary>加载令牌（AES 解密）</summary>
        public async Task<McpOAuthToken?> LoadTokenAsync(string mcpName)
        {
            var filePath = GetTokenPath(mcpName);
            if (!File.Exists(filePath)) return null;

            try
            {
                var encrypted = await File.ReadAllBytesAsync(filePath);
                var bytes = Unprotect(encrypted);
                var json = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<McpOAuthToken>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load OAuth token for {McpName}", mcpName);
                return null;
            }
        }

        /// <summary>AES 加密（基于机器密钥）</summary>
        private static byte[] Protect(byte[] data)
        {
            using var aes = Aes.Create();
            var key = DeriveKey();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        /// <summary>AES 解密</summary>
        private static byte[] Unprotect(byte[] encrypted)
        {
            using var aes = Aes.Create();
            var key = DeriveKey();
            aes.Key = key;

            var iv = new byte[aes.IV.Length];
            Array.Copy(encrypted, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(encrypted, iv.Length, encrypted.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var result = new MemoryStream();
            cs.CopyTo(result);
            return result.ToArray();
        }

        /// <summary>基于机器标识派生密钥</summary>
        private static byte[] DeriveKey()
        {
            var machineId = Environment.MachineName + Environment.UserName;
            using var pbkdf2 = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(machineId), Salt, 100000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
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
