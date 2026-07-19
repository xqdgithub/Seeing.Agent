namespace Seeing.Gateway.QQ;

/// <summary>
/// 从 QQ sessionId 反解主动出站目标（对齐 <see cref="QQSessionResolver"/>）
/// </summary>
public static class QQSessionTargetParser
{
    public static bool TryParse(string? sessionId, out ParsedQQMessage? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var id = sessionId.Trim();

        if (id.StartsWith("qq_group_", StringComparison.OrdinalIgnoreCase))
        {
            var rest = id["qq_group_".Length..];
            if (string.IsNullOrEmpty(rest) || rest.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            string groupOpenId;
            string? senderOpenId = null;
            var split = rest.LastIndexOf('_');
            if (split > 0 && split < rest.Length - 1)
            {
                groupOpenId = rest[..split];
                senderOpenId = rest[(split + 1)..];
            }
            else
            {
                groupOpenId = rest;
            }

            if (string.IsNullOrEmpty(groupOpenId))
                return false;

            target = new ParsedQQMessage
            {
                MessageType = "group",
                GroupOpenId = groupOpenId,
                SenderOpenId = senderOpenId,
                Text = "",
                MsgId = ""
            };
            return true;
        }

        if (id.StartsWith("qq_channel_", StringComparison.OrdinalIgnoreCase))
        {
            var channelId = id["qq_channel_".Length..];
            if (string.IsNullOrEmpty(channelId) || channelId.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            target = new ParsedQQMessage
            {
                MessageType = "guild",
                ChannelId = channelId,
                Text = "",
                MsgId = ""
            };
            return true;
        }

        if (id.StartsWith("qq_", StringComparison.OrdinalIgnoreCase)
            && !id.StartsWith("qq_group_", StringComparison.OrdinalIgnoreCase)
            && !id.StartsWith("qq_channel_", StringComparison.OrdinalIgnoreCase)
            && !id.Equals("qq_unknown", StringComparison.OrdinalIgnoreCase))
        {
            var sender = id["qq_".Length..];
            if (string.IsNullOrEmpty(sender))
                return false;

            target = new ParsedQQMessage
            {
                MessageType = "c2c",
                SenderOpenId = sender,
                Text = "",
                MsgId = ""
            };
            return true;
        }

        return false;
    }
}
