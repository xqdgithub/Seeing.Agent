using System.Text.Json;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.QQ;

public sealed class ParsedQQMessage
{
    public required string MessageType { get; init; }
    public required string MsgId { get; init; }
    public string? SenderOpenId { get; init; }
    public string? GroupOpenId { get; init; }
    public string? ChannelId { get; init; }
    public string? GuildId { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<QQAttachmentMeta> Attachments { get; init; } = [];
    /// <summary>作者是否为机器人（用于过滤自身回声）。</summary>
    public bool IsBotAuthor { get; init; }
    public JsonElement Raw { get; init; }
}

public sealed class QQAttachmentMeta
{
    public required string Url { get; init; }
    public string? ContentType { get; init; }
    public string? FileName { get; init; }
    public string? AsrText { get; init; }
    public bool IsVoice { get; init; }
}

/// <summary>
/// 解析 QQ WebSocket Dispatch 事件
/// </summary>
public static class QQMessageParser
{
    private static readonly HashSet<string> s_messageEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "C2C_MESSAGE_CREATE",
        "GROUP_AT_MESSAGE_CREATE",
        "GROUP_MESSAGE_CREATE", // 新版群聊事件（含 @）
        "AT_MESSAGE_CREATE",
        "DIRECT_MESSAGE_CREATE"
    };

    private static readonly HashSet<string> s_voiceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".amr", ".silk", ".slk"
    };

    public static bool IsMessageEvent(string? eventType) =>
        eventType != null && s_messageEvents.Contains(eventType);

    public static bool TryParse(string eventType, JsonElement d, out ParsedQQMessage? message)
    {
        message = null;
        if (!IsMessageEvent(eventType))
            return false;

        var messageType = eventType.ToUpperInvariant() switch
        {
            "C2C_MESSAGE_CREATE" => "c2c",
            "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE" => "group",
            "AT_MESSAGE_CREATE" => "guild",
            "DIRECT_MESSAGE_CREATE" => "dm",
            _ => "c2c"
        };

        var msgId = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(msgId))
            return false;

        string? sender = null;
        if (d.TryGetProperty("author", out var author))
        {
            sender = FirstString(author, "member_openid", "user_openid", "id");
        }

        sender ??= FirstString(d, "author", "member_openid", "user_openid");

        var text = d.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        text = StripAtMentions(text);

        var attachments = ParseAttachments(d);
        var textParts = new List<string>();

        if (TryFindQuotedElement(d, out var quoted) && quoted != null)
        {
            var quotedText = quoted.Value.TryGetProperty("content", out var qc) ? qc.GetString()?.Trim() : null;
            quotedText = string.IsNullOrEmpty(quotedText) ? null : StripAtMentions(quotedText);
            var quotedAtt = ParseAttachmentsFromArray(
                quoted.Value.TryGetProperty("attachments", out var qa) && qa.ValueKind == JsonValueKind.Array
                    ? qa
                    : default);

            if (!string.IsNullOrEmpty(quotedText))
                textParts.Add($"[quoted message: {quotedText}]");
            else if (quotedAtt.Count == 0)
                textParts.Add("[quoted message]");

            if (quotedAtt.Count > 0)
                attachments = [..quotedAtt, ..attachments];
        }

        if (!string.IsNullOrWhiteSpace(text))
            textParts.Add(text);

        var combinedText = string.Join("\n", textParts).Trim();

        // 群聊/频道 AT 事件：content 经常只有 <@!>，剥掉后为空；不能当空消息丢弃（私聊仍过滤纯空）
        var isAtConversation = messageType is "group" or "guild";
        if (string.IsNullOrWhiteSpace(combinedText) && attachments.Count == 0 && !isAtConversation)
            return false;

        // 机器人自身消息（避免 Ack/回复回声死循环）
        var isBot = false;
        if (d.TryGetProperty("author", out var authorForBot)
            && authorForBot.TryGetProperty("bot", out var botEl)
            && botEl.ValueKind is JsonValueKind.True)
        {
            isBot = true;
        }

        message = new ParsedQQMessage
        {
            MessageType = messageType,
            MsgId = msgId,
            SenderOpenId = sender,
            GroupOpenId = FirstStringIgnoreCase(d, "group_openid", "group_id"),
            ChannelId = FirstStringIgnoreCase(d, "channel_id"),
            GuildId = FirstStringIgnoreCase(d, "guild_id"),
            Text = combinedText,
            Attachments = attachments,
            IsBotAuthor = isBot,
            Raw = d
        };
        return true;
    }

    public static GatewayRequest ToGatewayRequest(
        ParsedQQMessage message,
        string sessionId,
        string? agentId,
        string? modeId,
        string? modelId,
        IReadOnlyList<GatewayContentPart>? extraParts = null)
    {
        var parts = new List<GatewayContentPart>();
        if (!string.IsNullOrWhiteSpace(message.Text))
            parts.Add(new GatewayTextContentPart(message.Text));

        if (extraParts != null)
            parts.AddRange(extraParts);

        if (parts.Count == 0)
            parts.Add(new GatewayTextContentPart(""));

        return new GatewayRequest
        {
            SessionId = sessionId,
            ChannelId = "qq",
            UserId = message.SenderOpenId,
            AgentId = agentId,
            ModeId = modeId,
            ModelId = modelId,
            Stream = true,
            Input = parts
        };
    }

    /// <summary>
    /// 按 message_scene.ext 中的 ref_msg_idx 查找被引用的 msg_elements 项。
    /// </summary>
    internal static bool TryFindQuotedElement(JsonElement d, out JsonElement? quoted)
    {
        quoted = null;
        if (!d.TryGetProperty("msg_elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            return false;

        string refIdx = "";
        string ownIdx = "";
        if (d.TryGetProperty("message_scene", out var scene)
            && scene.TryGetProperty("ext", out var ext)
            && ext.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ext.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var s = entry.GetString() ?? "";
                if (s.StartsWith("ref_msg_idx=", StringComparison.Ordinal))
                    refIdx = s["ref_msg_idx=".Length..];
                else if (s.StartsWith("msg_idx=", StringComparison.Ordinal))
                    ownIdx = s["msg_idx=".Length..];
            }
        }

        if (string.IsNullOrEmpty(refIdx))
            return false;

        foreach (var elem in elements.EnumerateArray())
        {
            if (elem.ValueKind != JsonValueKind.Object)
                continue;
            if (elem.TryGetProperty("msg_idx", out var idx) && idx.GetString() == refIdx)
            {
                quoted = elem;
                return true;
            }
        }

        foreach (var elem in elements.EnumerateArray())
        {
            if (elem.ValueKind != JsonValueKind.Object)
                continue;
            var elemIdx = elem.TryGetProperty("msg_idx", out var idx) ? idx.GetString() : null;
            if (!string.IsNullOrEmpty(elemIdx) && elemIdx != ownIdx)
            {
                quoted = elem;
                return true;
            }
        }

        return false;
    }

    private static List<QQAttachmentMeta> ParseAttachments(JsonElement d)
    {
        if (!d.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return ParseAttachmentsFromArray(arr);
    }

    private static List<QQAttachmentMeta> ParseAttachmentsFromArray(JsonElement arr)
    {
        var list = new List<QQAttachmentMeta>();
        if (arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            var fileName = FirstString(item, "filename", "file_name") ?? "";
            var contentType = FirstString(item, "content_type", "type") ?? "";
            var ext = Path.GetExtension(fileName);
            var isVoice = contentType.Equals("voice", StringComparison.OrdinalIgnoreCase)
                          || s_voiceExts.Contains(ext);

            var asr = FirstString(item, "asr_refer_text");
            // 仅用 url；不要用 content 冒充下载地址（部分载荷 content 非 URL）
            var url = FirstString(item, "url") ?? "";

            if (isVoice)
            {
                var wav = FirstString(item, "voice_wav_url");
                if (!string.IsNullOrEmpty(wav))
                {
                    url = wav;
                    if (!string.IsNullOrEmpty(fileName) && fileName.Contains('.', StringComparison.Ordinal))
                        fileName = Path.ChangeExtension(fileName, ".wav");
                    contentType = "audio/wav";
                }
                else if (string.IsNullOrEmpty(contentType) || contentType.Equals("voice", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = "audio/amr";
                }
            }

            // ASR 文本由 MediaFetcher 转成 Text part；仍保留附件供无 ASR 时下载
            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(asr))
                continue;

            list.Add(new QQAttachmentMeta
            {
                Url = url,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName,
                AsrText = asr,
                IsVoice = isVoice
            });
        }

        return list;
    }

    private static string StripAtMentions(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        // 频道多为数字 id；群聊 openid 为十六进制字符串，均需剥离
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<@!?[A-Za-z0-9_]+>",
            "").Trim();
    }

    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }

    /// <summary>大小写不敏感查找字符串属性（兼容网关字段名变体）。</summary>
    private static string? FirstStringIgnoreCase(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return null;
    }
}
