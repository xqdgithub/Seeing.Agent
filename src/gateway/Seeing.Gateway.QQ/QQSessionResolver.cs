namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ 会话命名（仅生成，不反解路由）。
/// 逻辑形式 <c>qq:group:{gid}</c> / <c>qq:direct:{uid}</c> / <c>qq:guild:{cid}</c>，
/// 再替换文件系统非法字符（含 <c>:</c>）后落盘。
/// </summary>
public static class QQSessionResolver
{
    public static string ResolveSessionId(ParsedQQMessage message, QQOptions options)
    {
        if (string.Equals(message.MessageType, "group", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.GroupOpenId))
        {
            var group = SanitizeSegment(message.GroupOpenId);
            if (options.ShareSessionInGroup)
                return ToFileSystemSafe($"qq:group:{group}");

            var member = string.IsNullOrWhiteSpace(message.SenderOpenId) ? "unknown" : SanitizeSegment(message.SenderOpenId);
            return ToFileSystemSafe($"qq:group:{group}:{member}");
        }

        if (string.Equals(message.MessageType, "guild", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.ChannelId))
        {
            return ToFileSystemSafe($"qq:guild:{SanitizeSegment(message.ChannelId)}");
        }

        if (string.Equals(message.MessageType, "dm", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(message.SenderOpenId))
        {
            var user = string.IsNullOrWhiteSpace(message.SenderOpenId) ? "unknown" : SanitizeSegment(message.SenderOpenId);
            return ToFileSystemSafe($"qq:direct:{user}");
        }

        return "qq_unknown";
    }

    internal static string GenerateRotatedSessionId(string conversationKey) =>
        $"{conversationKey}_{DateTime.Now:yyyyMMddHHmmss}";

    internal static string ToFileSystemSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "qq_unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == ':' || Array.IndexOf(invalid, buffer[i]) >= 0)
                buffer[i] = '_';
        }

        return new string(buffer);
    }

    private static string SanitizeSegment(string value) => ToFileSystemSafe(value);
}
