using Seeing.Agent.Abstractions.Channels;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

internal static class ToolJson
{
    public static string? ReadString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var prop)
            || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static int ReadInt(JsonElement arguments, string name, int fallback)
    {
        if (!arguments.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return fallback;
        return prop.TryGetInt32(out var value) ? value : fallback;
    }

    public static bool TryParseKind(string value, out ConversationKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "direct":
                kind = ConversationKind.Direct;
                return true;
            case "group":
                kind = ConversationKind.Group;
                return true;
            case "guild":
                kind = ConversationKind.Guild;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
