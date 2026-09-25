using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 令牌存储 - 使用 AES 加密（跨平台）。
    /// <para>
    /// 加密密钥为首次运行生成的 32 字节随机密钥，持久化于
    /// <c>%LocalAppData%\Seeing.Agent\mcp_oauth_key.bin</c>；密钥与密文分离存放，
    /// 不再从机器名/用户名派生，避免同机他人可推导。
    /// </para>
    /// </summary>
    public class McpOAuthStorage
    {
        private const int KeySizeBytes = 32;

        private static readonly ConcurrentDictionary<string, Lazy<byte[]>> s_keyCache =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ILogger<McpOAuthStorage> _logger;
        private readonly string _storagePath;
        private readonly string _keyFilePath;

        /// <param name="logger">日志</param>
        /// <param name="storageDirectory">令牌目录（默认 <c>~/.seeing/oauth-tokens</c>）</param>
        /// <param name="keyFilePath">密钥文件（默认 <c>%LocalAppData%\Seeing.Agent\mcp_oauth_key.bin</c>）</param>
        public McpOAuthStorage(
            ILogger<McpOAuthStorage> logger,
            string? storageDirectory = null,
            string? keyFilePath = null)
        {
            _logger = logger;
            _storagePath = storageDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "oauth-tokens");
            _keyFilePath = keyFilePath ?? GetDefaultKeyFilePath();

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>默认密钥文件路径。</summary>
        public static string GetDefaultKeyFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");
            }

            return Path.Combine(localAppData, "Seeing.Agent", "mcp_oauth_key.bin");
        }

        /// <summary>保存令牌（AES 加密）</summary>
        public async Task SaveTokenAsync(string mcpName, McpOAuthToken token)
        {
            var filePath = GetTokenPath(mcpName);
            var json = JsonSerializer.Serialize(token);
            var bytes = Encoding.UTF8.GetBytes(json);

            var encrypted = Protect(bytes);
            await File.WriteAllBytesAsync(filePath, encrypted).ConfigureAwait(false);
            _logger.LogDebug("Saved OAuth token for {McpName}", mcpName);
        }

        /// <summary>加载令牌（AES 解密）</summary>
        public async Task<McpOAuthToken?> LoadTokenAsync(string mcpName)
        {
            var filePath = GetTokenPath(mcpName);
            if (!File.Exists(filePath)) return null;

            try
            {
                var encrypted = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
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

        /// <summary>AES-CBC 加密（随机 IV 前置）。</summary>
        private byte[] Protect(byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = GetKey();
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

        /// <summary>AES-CBC 解密（读取前置 IV）。</summary>
        private byte[] Unprotect(byte[] encrypted)
        {
            using var aes = Aes.Create();
            aes.Key = GetKey();

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

        /// <summary>获取本机持久化密钥（首次生成并落盘，进程内缓存）。</summary>
        private byte[] GetKey()
            => s_keyCache.GetOrAdd(_keyFilePath, path => new Lazy<byte[]>(() => LoadOrGenerateKey(path))).Value;

        private static byte[] LoadOrGenerateKey(string keyFilePath)
        {
            if (File.Exists(keyFilePath))
            {
                var existing = File.ReadAllBytes(keyFilePath);
                if (existing.Length == KeySizeBytes)
                    return existing;
            }

            var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            var dir = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 先写临时文件再原子替换，避免中断留下半截密钥
            var tmpPath = keyFilePath + ".tmp";
            File.WriteAllBytes(tmpPath, key);
            File.Move(tmpPath, keyFilePath, overwrite: true);
            return key;
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
