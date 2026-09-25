using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// PKCE（RFC 7636）工具：生成 code_verifier 与 S256 code_challenge。
    /// </summary>
    public static class McpOAuthPkce
    {
        /// <summary>生成 URL 安全的 code_verifier（32 字节随机数，Base64URL 编码后为 43 字符）。</summary>
        public static string CreateCodeVerifier()
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        }

        /// <summary>由 code_verifier 计算 S256 code_challenge。</summary>
        public static string CreateCodeChallenge(string codeVerifier)
        {
            ArgumentException.ThrowIfNullOrEmpty(codeVerifier);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
            return Base64UrlEncode(hash);
        }

        internal static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
