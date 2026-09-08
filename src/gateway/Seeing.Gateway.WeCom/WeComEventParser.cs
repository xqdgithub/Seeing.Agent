using System.Text.Json;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 解析企微 aibot_event_callback 事件
/// </summary>
public static class WeComEventParser
{
    public const string EnterChatEventType = "enter_chat";
    public const string TemplateCardEventType = "template_card_event";
    public const string DisconnectedEventType = "disconnected_event";

    public static bool TryParseEnterChat(WeComWsFrame frame, out ParsedWeComEnterChat? parsed)
    {
        parsed = null;
        if (!TryParseEventBody(frame, out var message))
            return false;

        var eventType = message.Event?.EventType?.ToLowerInvariant();
        if (!string.Equals(eventType, EnterChatEventType, StringComparison.OrdinalIgnoreCase))
            return false;

        parsed = new ParsedWeComEnterChat
        {
            Frame = frame,
            MessageId = message.MsgId ?? string.Empty,
            UserId = message.From?.UserId ?? string.Empty,
            ChatId = message.ChatId ?? string.Empty,
            ChatType = message.ChatType ?? "single"
        };
        return true;
    }

    public static bool TryParseTemplateCardEvent(WeComWsFrame frame, out ParsedWeComTemplateCardEvent? parsed)
    {
        parsed = null;
        if (!TryParseEventBody(frame, out var message))
            return false;

        var eventType = message.Event?.EventType?.ToLowerInvariant();
        if (!string.Equals(eventType, TemplateCardEventType, StringComparison.OrdinalIgnoreCase))
            return false;

        var cardEvent = message.Event?.TemplateCardEvent;
        if (cardEvent == null || string.IsNullOrWhiteSpace(cardEvent.TaskId))
            return false;

        parsed = new ParsedWeComTemplateCardEvent
        {
            Frame = frame,
            MessageId = message.MsgId ?? string.Empty,
            UserId = message.From?.UserId ?? string.Empty,
            ChatId = message.ChatId ?? string.Empty,
            ChatType = message.ChatType ?? "single",
            TaskId = cardEvent.TaskId,
            EventKey = cardEvent.EventKey ?? string.Empty,
            CardType = cardEvent.CardType ?? string.Empty
        };
        return true;
    }

    public static bool TryParseDisconnectedEvent(WeComWsFrame frame, out ParsedWeComDisconnectedEvent? parsed)
    {
        parsed = null;
        if (!TryParseEventBody(frame, out var message))
            return false;

        var eventType = message.Event?.EventType?.ToLowerInvariant();
        if (!string.Equals(eventType, DisconnectedEventType, StringComparison.OrdinalIgnoreCase))
            return false;

        parsed = new ParsedWeComDisconnectedEvent
        {
            Frame = frame,
            MessageId = message.MsgId ?? string.Empty,
            AiBotId = message.AiBotId ?? string.Empty
        };
        return true;
    }

    private static bool TryParseEventBody(WeComWsFrame frame, out WeComIncomingMessage message)
    {
        message = null!;
        if (frame.Body == null)
            return false;

        var parsed = frame.Body.Value.Deserialize<WeComIncomingMessage>(WeComWsJson.Options);
        if (parsed == null)
            return false;

        if (!string.Equals(parsed.MsgType, "event", StringComparison.OrdinalIgnoreCase)
            && parsed.Event == null)
        {
            return false;
        }

        message = parsed;
        return true;
    }
}

public sealed class ParsedWeComEnterChat
{
    public required WeComWsFrame Frame { get; init; }

    public required string MessageId { get; init; }

    public required string UserId { get; init; }

    public required string ChatId { get; init; }

    public required string ChatType { get; init; }
}

public sealed class ParsedWeComTemplateCardEvent
{
    public required WeComWsFrame Frame { get; init; }

    public required string MessageId { get; init; }

    public required string UserId { get; init; }

    public required string ChatId { get; init; }

    public required string ChatType { get; init; }

    public required string TaskId { get; init; }

    public required string EventKey { get; init; }

    public required string CardType { get; init; }
}

public sealed class ParsedWeComDisconnectedEvent
{
    public required WeComWsFrame Frame { get; init; }

    public required string MessageId { get; init; }

    public required string AiBotId { get; init; }
}
