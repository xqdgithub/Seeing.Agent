using System.Security.Cryptography;
using System.Text;

namespace Seeing.Agent.Core.Helpers;

/// <summary>哈希计算辅助工具。</summary>
public static class HashHelper
{
    /// <summary>计算输入字符串的 SHA256 十六进制小写摘要。</summary>
    public static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
