namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 sessionId 映射（使用文件系统安全字符，不含 <c>:</c>）
/// </summary>
public static class WeComSessionResolver
{
    public static string ResolveConversationKey(ParsedWeComMessage message, WeComOptions options) =>
        ResolveSessionId(message, options);

    public static string ResolveConversationKey(ParsedWeComEnterChat enterChat, WeComOptions options)
    {
        if (string.Equals(enterChat.ChatType, "group", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(enterChat.ChatId)
            && options.ShareSessionInGroup)
        {
            return $"wecom_group_{SanitizeSegment(enterChat.ChatId)}";
        }

        if (!string.IsNullOrWhiteSpace(enterChat.UserId))
            return $"wecom_{SanitizeSegment(enterChat.UserId)}";

        if (!string.IsNullOrWhiteSpace(enterChat.ChatId))
            return $"wecom_{SanitizeSegment(enterChat.ChatId)}";

        return "wecom_unknown";
    }

    /// <summary>解析基础 sessionId（不含 idle 轮换后缀）</summary>
    public static string ResolveSessionId(ParsedWeComMessage message, WeComOptions options)
    {
        if (string.Equals(message.ChatType, "group", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.ChatId)
            && options.ShareSessionInGroup)
        {
            return $"wecom_group_{SanitizeSegment(message.ChatId)}";
        }

        if (!string.IsNullOrWhiteSpace(message.UserId))
            return $"wecom_{SanitizeSegment(message.UserId)}";

        if (!string.IsNullOrWhiteSpace(message.ChatId))
            return $"wecom_{SanitizeSegment(message.ChatId)}";

        return "wecom_unknown";
    }

    internal static string GenerateRotatedSessionId(string conversationKey) =>
        $"{conversationKey}_{DateTime.UtcNow:yyyyMMddHHmmss}";

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(invalid, buffer[i]) >= 0 || buffer[i] == ':')
                buffer[i] = '_';
        }

        return new string(buffer);
    }
}
