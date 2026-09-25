using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// OAuth 令牌存储 - 使用 AES-GCM 认证加密（跨平台）。
    /// <para>
    /// 加密密钥为首次运行生成的 32 字节随机密钥，持久化于
    /// <c>%LocalAppData%\Seeing.Agent\mcp_oauth_key.bin</c>；密钥与密文分离存放，
    /// 不再从机器名/用户名派生，避免同机他人可推导。
    /// </para>
    /// <para>
    /// 密文格式（AES-GCM）：<c>版本头(4B "MOA2") || nonce(12B) || tag(16B) || ciphertext</c>。
    /// 认证标签保证篡改/错误密钥会立即、明确地解密失败；读取时向后兼容旧版 AES-CBC 格式
    /// （随机 IV 前置、无认证），以支持升级迁移。
    /// </para>
    /// </summary>
    public class McpOAuthStorage
    {
        private const int KeySizeBytes = 32;
        private const int GcmNonceSizeBytes = 12;
        private const int GcmTagSizeBytes = 16;

        /// <summary>新版 AesGcm 密文版本头（含格式识别与版本信息）。</summary>
        private static readonly byte[] s_gcmHeader = { (byte)'M', (byte)'O', (byte)'A', (byte)'2' };

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
            catch (AuthenticationTagMismatchException ex)
            {
                // GCM 认证失败：密钥不匹配或数据被篡改，立即明确失败（不返回任何明文）
                _logger.LogWarning(ex, "OAuth 令牌认证失败（密钥不匹配或数据被篡改），拒绝加载: {McpName}", mcpName);
                return null;
            }
            catch (CryptographicException ex)
            {
                // 旧版 CBC 的 padding 异常等：同样视为密钥不匹配/数据损坏
                _logger.LogWarning(ex, "OAuth 令牌解密失败（密钥不匹配或数据损坏），拒绝加载: {McpName}", mcpName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load OAuth token for {McpName}", mcpName);
                return null;
            }
        }

        /// <summary>加密令牌：AES-GCM（版本头 + 随机 nonce + 认证标签 + 密文）。</summary>
        private byte[] Protect(byte[] data)
        {
            var nonce = RandomNumberGenerator.GetBytes(GcmNonceSizeBytes);
            var ciphertext = new byte[data.Length];
            var tag = new byte[GcmTagSizeBytes];

            using var gcm = new AesGcm(GetKey(), GcmTagSizeBytes);
            gcm.Encrypt(nonce, data, ciphertext, tag);

            var output = new byte[s_gcmHeader.Length + GcmNonceSizeBytes + GcmTagSizeBytes + ciphertext.Length];
            var offset = 0;
            Buffer.BlockCopy(s_gcmHeader, 0, output, offset, s_gcmHeader.Length);
            offset += s_gcmHeader.Length;
            Buffer.BlockCopy(nonce, 0, output, offset, nonce.Length);
            offset += nonce.Length;
            Buffer.BlockCopy(tag, 0, output, offset, tag.Length);
            offset += tag.Length;
            Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length);
            return output;
        }

        /// <summary>解密令牌：自动识别新版 AES-GCM 与旧版 AES-CBC 格式。</summary>
        private byte[] Unprotect(byte[] encrypted)
        {
            return IsGcmFormat(encrypted)
                ? UnprotectGcm(encrypted)
                : UnprotectLegacyCbc(encrypted);
        }

        private static bool IsGcmFormat(byte[] data)
        {
            if (data.Length < s_gcmHeader.Length + GcmNonceSizeBytes + GcmTagSizeBytes)
                return false;

            return data.AsSpan(0, s_gcmHeader.Length).SequenceEqual(s_gcmHeader);
        }

        private byte[] UnprotectGcm(byte[] encrypted)
        {
            var offset = s_gcmHeader.Length;
            var nonce = encrypted.AsSpan(offset, GcmNonceSizeBytes);
            offset += GcmNonceSizeBytes;
            var tag = encrypted.AsSpan(offset, GcmTagSizeBytes);
            offset += GcmTagSizeBytes;
            var ciphertext = encrypted.AsSpan(offset);
            var plaintext = new byte[ciphertext.Length];

            using var gcm = new AesGcm(GetKey(), GcmTagSizeBytes);
            // 认证标签不匹配时抛 AuthenticationTagMismatchException
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        /// <summary>旧版 AES-CBC 解密（随机 IV 前置、无认证），仅用于向后兼容读取。</summary>
        private byte[] UnprotectLegacyCbc(byte[] encrypted)
        {
            using var aes = Aes.Create();
            aes.Key = GetKey();

            var ivSize = aes.BlockSize / 8;
            if (encrypted.Length <= ivSize)
                throw new CryptographicException("旧版 OAuth 令牌密文长度非法");

            var iv = new byte[ivSize];
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
