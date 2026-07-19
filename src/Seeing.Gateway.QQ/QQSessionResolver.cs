namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ sessionId 映射（文件系统安全字符，不含 <c>:</c>）
/// </summary>
public static class QQSessionResolver
{
    public static string ResolveSessionId(ParsedQQMessage message, QQOptions options)
    {
        if (string.Equals(message.MessageType, "group", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.GroupOpenId))
        {
            if (options.ShareSessionInGroup)
                return $"qq_group_{SanitizeSegment(message.GroupOpenId)}";

            var member = string.IsNullOrWhiteSpace(message.SenderOpenId) ? "unknown" : SanitizeSegment(message.SenderOpenId);
            return $"qq_group_{SanitizeSegment(message.GroupOpenId)}_{member}";
        }

        if ((string.Equals(message.MessageType, "guild", StringComparison.OrdinalIgnoreCase)
             || string.Equals(message.MessageType, "dm", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(message.ChannelId))
        {
            return $"qq_channel_{SanitizeSegment(message.ChannelId)}";
        }

        if (!string.IsNullOrWhiteSpace(message.SenderOpenId))
            return $"qq_{SanitizeSegment(message.SenderOpenId)}";

        return "qq_unknown";
    }

    internal static string GenerateRotatedSessionId(string conversationKey) =>
        $"{conversationKey}_{DateTime.Now:yyyyMMddHHmmss}";

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
