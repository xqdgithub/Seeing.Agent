using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 群聊 @mention 清理（普通消息与斜杠命令共用）
/// </summary>
internal static partial class WeComGroupMentionNormalizer
{
    internal static string NormalizeUserText(string text, string chatType)
    {
        text = text.Trim();
        if (!string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase))
            return text;

        text = GroupLeadingMention().Replace(text, string.Empty).Trim();
        if (text.StartsWith('/'))
            text = GroupMentionAfterSlash().Replace(text, string.Empty).Trim();

        return text;
    }

    [GeneratedRegex(@"^@\S+\s+", RegexOptions.CultureInvariant)]
    private static partial Regex GroupLeadingMention();

    [GeneratedRegex(@"@\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupMentionAfterSlash();
}
