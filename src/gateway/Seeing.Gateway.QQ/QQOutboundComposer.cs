using System.Text.RegularExpressions;

namespace Seeing.Gateway.QQ;

public enum QQOutboundMediaKind
{
    Image = 1,
    Video = 2,
    Voice = 3,
    File = 4
}

public sealed record QQOutboundMediaItem(
    QQOutboundMediaKind Kind,
    string Source,
    string? FileName = null);

public sealed record QQOutboundPayload(
    string CleanText,
    IReadOnlyList<QQOutboundMediaItem> Media);

/// <summary>
/// 从 Agent 终态文本中编排 QQ 出站文本 + 媒体标记。
/// </summary>
public static partial class QQOutboundComposer
{
    private static readonly HashSet<string> s_voiceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".amr", ".silk", ".slk"
    };

    public static QQOutboundPayload Compose(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new QQOutboundPayload("", Array.Empty<QQOutboundMediaItem>());

        var media = new List<QQOutboundMediaItem>();
        var clean = text;

        clean = MediaTagPattern().Replace(clean, m =>
        {
            var kindName = m.Groups[1].Value;
            var source = m.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(source))
                return "";

            var kind = kindName.ToLowerInvariant() switch
            {
                "image" => QQOutboundMediaKind.Image,
                "video" => QQOutboundMediaKind.Video,
                "file" => QQOutboundMediaKind.File,
                "voice" or "audio" => QQOutboundMediaKind.Voice,
                _ => QQOutboundMediaKind.File
            };
            var fileName = GuessFileName(source);
            media.Add(new QQOutboundMediaItem(kind, source, fileName));
            return "";
        });

        clean = MarkdownImagePattern().Replace(clean, m =>
        {
            var url = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(url))
                media.Add(new QQOutboundMediaItem(QQOutboundMediaKind.Image, url, GuessFileName(url)));
            return "";
        });

        return new QQOutboundPayload(clean.Trim(), media);
    }

    /// <summary>
    /// 映射到 QQ rich-media <c>file_type</c>；语音扩展名强制为 3。
    /// </summary>
    public static int ResolveMediaType(QQOutboundMediaKind kind, string? sourceOrFileName = null)
    {
        var pathHint = sourceOrFileName ?? "";
        var ext = Path.GetExtension(StripQuery(pathHint));
        if (s_voiceExts.Contains(ext))
            return QQHttpApiClientMediaTypes.Audio;

        return kind switch
        {
            QQOutboundMediaKind.Image => QQHttpApiClientMediaTypes.Image,
            QQOutboundMediaKind.Video => QQHttpApiClientMediaTypes.Video,
            QQOutboundMediaKind.Voice => QQHttpApiClientMediaTypes.Audio,
            _ => QQHttpApiClientMediaTypes.File
        };
    }

    public static bool IsHttpUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static bool IsDataUrl(string source) =>
        source.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocalFile(string source) =>
        !IsHttpUrl(source) && !IsDataUrl(source) && File.Exists(source);

    private static string? GuessFileName(string source)
    {
        if (IsDataUrl(source))
            return null;
        try
        {
            if (IsHttpUrl(source) && Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                var name = Path.GetFileName(uri.AbsolutePath);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }

            return Path.GetFileName(source);
        }
        catch
        {
            return null;
        }
    }

    private static string StripQuery(string path)
    {
        var q = path.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? path[..q] : path;
    }

    [GeneratedRegex(@"\[(Image|Video|File|Voice|Audio):\s*([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaTagPattern();

    [GeneratedRegex(@"!\[[^\]]*\]\((https?://[^)\s]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImagePattern();
}

/// <summary>与 <see cref="Connection.QQHttpApiClient"/> 常量对齐，避免 Composer 循环引用命名。</summary>
internal static class QQHttpApiClientMediaTypes
{
    public const int Image = 1;
    public const int Video = 2;
    public const int Audio = 3;
    public const int File = 4;
}
