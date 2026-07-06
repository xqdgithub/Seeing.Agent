using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 AI Bot WebSocket 帧信封
/// </summary>
public sealed class WeComWsFrame
{
    [JsonPropertyName("cmd")]
    public string? Cmd { get; set; }

    [JsonPropertyName("headers")]
    public WeComWsHeaders? Headers { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    [JsonPropertyName("errcode")]
    public int ErrCode { get; set; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; set; }
}

public sealed class WeComWsHeaders
{
    [JsonPropertyName("req_id")]
    public string? ReqId { get; set; }
}

public static class WeComWsCommands
{
    public const string Subscribe = "aibot_subscribe";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string MsgCallback = "aibot_msg_callback";
    public const string EventCallback = "aibot_event_callback";
    public const string RespondMsg = "aibot_respond_msg";
    public const string RespondWelcome = "aibot_respond_welcome_msg";
    public const string RespondUpdate = "aibot_respond_update_msg";
}

public sealed class WeComSubscribeBody
{
    [JsonPropertyName("bot_id")]
    public required string BotId { get; init; }

    [JsonPropertyName("secret")]
    public required string Secret { get; init; }
}

public sealed class WeComRespondStreamBody
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; init; } = "stream";

    [JsonPropertyName("stream")]
    public required WeComStreamPayload Stream { get; init; }
}

public sealed class WeComStreamPayload
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("finish")]
    public bool Finish { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

public sealed class WeComIncomingMessage
{
    [JsonPropertyName("msgid")]
    public string? MsgId { get; set; }

    [JsonPropertyName("aibotid")]
    public string? AiBotId { get; set; }

    [JsonPropertyName("chatid")]
    public string? ChatId { get; set; }

    [JsonPropertyName("chattype")]
    public string? ChatType { get; set; }

    [JsonPropertyName("from")]
    public WeComIncomingFrom? From { get; set; }

    [JsonPropertyName("msgtype")]
    public string? MsgType { get; set; }

    [JsonPropertyName("text")]
    public WeComTextPayload? Text { get; set; }

    [JsonPropertyName("image")]
    public WeComEncryptedMediaPayload? Image { get; set; }

    [JsonPropertyName("voice")]
    public WeComVoicePayload? Voice { get; set; }

    [JsonPropertyName("file")]
    public WeComEncryptedMediaPayload? File { get; set; }

    [JsonPropertyName("video")]
    public WeComEncryptedMediaPayload? Video { get; set; }

    [JsonPropertyName("quote")]
    public WeComQuotePayload? Quote { get; set; }

    [JsonPropertyName("event")]
    public WeComEventPayload? Event { get; set; }
}

public sealed class WeComEncryptedMediaPayload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("aeskey")]
    public string? AesKey { get; set; }

    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    [JsonPropertyName("media_id")]
    public string? MediaId { get; set; }
}

public sealed class WeComVoicePayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("aeskey")]
    public string? AesKey { get; set; }
}

public sealed class WeComIncomingFrom
{
    [JsonPropertyName("userid")]
    public string? UserId { get; set; }
}

public sealed class WeComTextPayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public sealed class WeComQuotePayload
{
    [JsonPropertyName("msgtype")]
    public string? MsgType { get; set; }

    [JsonPropertyName("text")]
    public WeComTextPayload? Text { get; set; }

    [JsonPropertyName("image")]
    public WeComEncryptedMediaPayload? Image { get; set; }

    [JsonPropertyName("voice")]
    public WeComVoicePayload? Voice { get; set; }

    [JsonPropertyName("file")]
    public WeComEncryptedMediaPayload? File { get; set; }

    [JsonPropertyName("video")]
    public WeComEncryptedMediaPayload? Video { get; set; }

    [JsonPropertyName("mixed")]
    public WeComMixedPayload? Mixed { get; set; }
}

public sealed class WeComMixedPayload
{
    [JsonPropertyName("msg_item")]
    public List<WeComMixedMessageItem>? MsgItem { get; set; }
}

public sealed class WeComMixedMessageItem
{
    [JsonPropertyName("msgtype")]
    public string? MsgType { get; set; }

    [JsonPropertyName("text")]
    public WeComTextPayload? Text { get; set; }

    [JsonPropertyName("image")]
    public WeComEncryptedMediaPayload? Image { get; set; }

    [JsonPropertyName("voice")]
    public WeComVoicePayload? Voice { get; set; }

    [JsonPropertyName("file")]
    public WeComEncryptedMediaPayload? File { get; set; }

    [JsonPropertyName("video")]
    public WeComEncryptedMediaPayload? Video { get; set; }
}

public sealed class WeComEventPayload
{
    [JsonPropertyName("eventtype")]
    public string? EventType { get; set; }

    [JsonPropertyName("template_card_event")]
    public WeComTemplateCardEventPayload? TemplateCardEvent { get; set; }
}

public sealed class WeComTemplateCardEventPayload
{
    [JsonPropertyName("card_type")]
    public string? CardType { get; set; }

    [JsonPropertyName("event_key")]
    public string? EventKey { get; set; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }
}

public static class WeComWsJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
