using System.Text.RegularExpressions;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ 明文消息不允许含 URL；提供多级清洗。
/// </summary>
public static partial class QQTextSanitizer
{
    private const string Omitted = "[链接已省略]";

    public static (string Text, bool HadUrl) Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ("", false);
        var sanitized = UrlPattern().Replace(text, Omitted);
        return (sanitized, !string.Equals(sanitized, text, StringComparison.Ordinal));
    }

    public static (string Text, bool HadUrl) AggressiveSanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ("", false);
        var sanitized = BareDomainPattern().Replace(text, Omitted);
        return (sanitized, !string.Equals(sanitized, text, StringComparison.Ordinal));
    }

    /// <summary>
    /// 从回复文本中提取可出站图片 URL，并返回去掉图片标记后的正文。
    /// 委托 <see cref="QQOutboundComposer"/>（兼容旧调用）。
    /// </summary>
    public static (string CleanText, IReadOnlyList<string> ImageUrls) ExtractImages(string text)
    {
        var payload = QQOutboundComposer.Compose(text);
        var urls = payload.Media
            .Where(m => m.Kind == QQOutboundMediaKind.Image)
            .Select(m => m.Source)
            .ToList();
        return (payload.CleanText, urls);
    }

    public static IEnumerable<string> SplitChunks(string text, int maxLen = 1800)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var len = Math.Min(maxLen, text.Length - start);
            if (start + len < text.Length)
            {
                var slice = text.AsSpan(start, len);
                var breakAt = -1;
                for (var i = slice.Length - 1; i > maxLen / 2; i--)
                {
                    var c = slice[i];
                    if (c is '\n' or ' ' or '。' or '！' or '？' or '.' or '!' or '?')
                    {
                        breakAt = i;
                        break;
                    }
                }
                if (breakAt > 0)
                    len = breakAt + 1;
            }

            yield return text.Substring(start, len).Trim();
            start += len;
        }
    }

    [GeneratedRegex(@"https?://[^\s]+|www\.[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"https?://[^\s]+|www\.[^\s]+|\b[\w][\w.-]*\.(?:com|cn|org|net|edu|gov|io|co|cc|tv|me|info|biz|app|dev|top|xyz|site|vip|shop|tech|club|pro|live|mobi|asia|wiki)(?:\.[a-z]{2,3})?\b(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDomainPattern();
}
