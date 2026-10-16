using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Tools.Support;
using System.Text;

namespace Seeing.Agent.Files;

/// <summary>内容分类：Kind / MIME / 编码 / 可编辑性。</summary>
internal static class FileContentClassifier
{
    /// <summary>判定内容类型。</summary>
    public static FileContentKind Classify(string path, string mime)
    {
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime != "image/svg+xml")
            return FileContentKind.Image;
        if (FileContentSupport.IsBinaryByExtension(path))
            return FileContentKind.Binary;
        if (string.Equals(mime, "text/markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            return FileContentKind.Markdown;
        return FileContentKind.Text;
    }

    /// <summary>探测编码并判断是否可编辑（仅 UTF-8 / ASCII）。</summary>
    public static (string? encoding, bool editable) DetectEditable(byte[] bytes)
    {
        if (bytes.Length == 0)
            return ("utf-8", true);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ("utf-8", true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ("utf-16le", false);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ("utf-16be", false);

        // 严格 UTF-8 校验（拒绝非法序列 → 视为非 UTF-8）
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(bytes);
            return ("utf-8", true);
        }
        catch (DecoderFallbackException)
        {
            return (null, false);
        }
    }
}
