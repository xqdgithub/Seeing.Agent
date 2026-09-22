using System.Globalization;
using Seeing.Agent.Core.Tools.FileSystem;

namespace Seeing.Agent.Tui.Services;

/// <summary>待附加附件：文件名、MIME、Base64 字节与体积。</summary>
public sealed record TuiAttachment(string FileName, string MimeType, string Base64Data, long Size);

/// <summary>
/// 附件解析服务：解析输入首部的 <c>@path</c> 挂载语法，并将本地文件读取为可提交附件。
/// <para>仅识别输入开头（允许前置空白）的 <c>@path</c>，支持连续多个与 <c>@"含 空格 的路径"</c> 引号形式。</para>
/// </summary>
public sealed class TuiAttachmentResolver
{
    /// <summary>单个附件体积上限（10 MB）：Base64 后约 1.33 倍，需常驻内存与请求体。</summary>
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;

    /// <summary>未识别到附件时的空路径集合。</summary>
    private static readonly IReadOnlyList<string> NoPaths = [];

    /// <summary>仓库 helper 未覆盖的补充 MIME（<see cref="FileSystemHelper"/> 缺 .csv）。</summary>
    private static readonly Dictionary<string, string> SupplementalMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = "text/csv",
    };

    /// <summary>
    /// 解析输入首部的 <c>@path</c>（支持 <c>@"含 空格 的路径"</c>）；返回剩余文本与待附加路径。
    /// 无 <c>@</c> 时返回 <c>false</c> 且 <paramref name="remainingText"/> 保持原文本、<paramref name="paths"/> 为空。
    /// </summary>
    public bool TryParseInline(string text, out string remainingText, out IReadOnlyList<string> paths)
    {
        if (string.IsNullOrEmpty(text))
        {
            remainingText = string.Empty;
            paths = NoPaths;
            return false;
        }

        var parsed = new List<string>();
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        while (index < text.Length && text[index] == '@')
        {
            if (!TryReadPath(text, index, out var path, out var next))
                break;

            parsed.Add(path);
            index = next;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
        }

        if (parsed.Count == 0)
        {
            remainingText = text;
            paths = NoPaths;
            return false;
        }

        remainingText = text[index..].Trim();
        paths = parsed;
        return true;
    }

    /// <summary>读取本地文件为附件（Base64）。MIME 由扩展名映射，未知扩展名回退内容采样，最终回退 <c>application/octet-stream</c>。</summary>
    public async Task<TuiAttachment> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"附件不存在：{path}", fullPath);

        // 先查体积再读取：避免超大文件被整块读入内存并 Base64 常驻。
        var length = new FileInfo(fullPath).Length;
        if (length > MaxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"附件过大：{FormatSize(length)}，超过上限 {FormatSize(MaxAttachmentBytes)}（{path}）");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        var mimeType = ResolveMimeType(fullPath, bytes);

        return new TuiAttachment(
            Path.GetFileName(fullPath),
            mimeType,
            Convert.ToBase64String(bytes),
            bytes.LongLength);
    }

    /// <summary>是否图片 MIME（<c>image/</c> 前缀）。</summary>
    public static bool IsImage(string mimeType)
        => !string.IsNullOrEmpty(mimeType)
           && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>附件体积（人类可读，1024 进制）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
        if (bytes < 1024L * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} KB", bytes / 1024.0);
        if (bytes < 1024L * 1024 * 1024)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", bytes / (1024.0 * 1024));
        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} GB", bytes / (1024.0 * 1024 * 1024));
    }

    /// <summary>从 <paramref name="at"/>（指向 <c>@</c>）读取一个路径；失败时返回 false 且不消费。</summary>
    private static bool TryReadPath(string text, int at, out string path, out int next)
    {
        path = string.Empty;
        next = at;

        var index = at + 1;
        if (index >= text.Length)
            return false;

        if (text[index] == '"')
        {
            var close = text.IndexOf('"', index + 1);
            if (close < 0)
            {
                // 引号未闭合：退回按空白终止，避免吞掉整段输入
                var end = FindWhitespace(text, index + 1);
                path = text[(index + 1)..end];
                next = end;
                return path.Length > 0;
            }

            path = text[(index + 1)..close];
            next = close + 1;
            return path.Length > 0;
        }

        var whitespace = FindWhitespace(text, index);
        path = text[index..whitespace];
        next = whitespace;
        return path.Length > 0;
    }

    private static int FindWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string ResolveMimeType(string fullPath, byte[] bytes)
    {
        var extension = Path.GetExtension(fullPath);
        if (SupplementalMimeTypes.TryGetValue(extension, out var supplemental))
            return supplemental;

        var mapped = FileSystemHelper.GetMimeType(fullPath);
        if (!string.Equals(mapped, "application/octet-stream", StringComparison.Ordinal))
            return mapped;

        return SniffMimeType(bytes) ?? "application/octet-stream";
    }

    /// <summary>内容采样：按魔数识别常见格式，未识别返回 null。</summary>
    private static string? SniffMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 4 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return "image/bmp";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        if (bytes.Length >= 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F')
            return "application/pdf";

        return null;
    }
}
