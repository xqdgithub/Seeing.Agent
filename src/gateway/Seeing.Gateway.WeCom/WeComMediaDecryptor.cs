using System.Security.Cryptography;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微长连接媒体 AES-256-CBC 解密（IV = aeskey 前 16 字节，PKCS7）
/// </summary>
public static class WeComMediaDecryptor
{
    public static byte[] Decrypt(byte[] encrypted, string aesKeyBase64OrRaw)
    {
        var keyMaterial = DecodeKeyMaterial(aesKeyBase64OrRaw);
        if (keyMaterial.Length < 32)
            throw new InvalidOperationException("aeskey 长度不足，无法解密媒体");

        var key = keyMaterial.AsSpan(0, 32).ToArray();
        var iv = keyMaterial.AsSpan(0, 16).ToArray();

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    private static byte[] DecodeKeyMaterial(string aesKey)
    {
        if (string.IsNullOrWhiteSpace(aesKey))
            throw new ArgumentException("aeskey 不能为空", nameof(aesKey));

        try
        {
            var decoded = Convert.FromBase64String(aesKey);
            if (decoded.Length >= 32)
                return decoded;
        }
        catch (FormatException)
        {
            // fall through to UTF8 bytes
        }

        return System.Text.Encoding.UTF8.GetBytes(aesKey);
    }
}
