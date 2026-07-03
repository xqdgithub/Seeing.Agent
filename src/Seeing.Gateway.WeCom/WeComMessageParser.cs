using System.Text.Json.Serialization;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 解析企微入站消息为 Gateway 输入
/// </summary>
public static class WeComMessageParser
{
    public static bool TryParseText(WeComIncomingContext context, out ParsedWeComMessage? parsed)
    {
        parsed = null;
        var message = context.Message;
        var msgType = message.MsgType?.ToLowerInvariant();

        if (msgType == "event")
            return false;

        if (msgType != "text")
            return false;

        var text = message.Text?.Content;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        parsed = CreateParsed(context, BuildTextParts(text.Trim()));
        return true;
    }

    public static async Task<(bool Ok, ParsedWeComMessage? Parsed)> TryParseAsync(
        WeComIncomingContext context,
        WeComMediaFetcher mediaFetcher,
        CancellationToken cancellationToken = default)
    {
        var message = context.Message;
        var msgType = message.MsgType?.ToLowerInvariant();

        if (msgType == "event")
            return (false, null);

        var parts = new List<GatewayContentPart>();

        switch (msgType)
        {
            case "text":
            {
                var text = message.Text?.Content;
                if (string.IsNullOrWhiteSpace(text))
                    return (false, null);
                parts.Add(new GatewayTextContentPart(text.Trim()));
                break;
            }

            case "voice":
            {
                if (!string.IsNullOrWhiteSpace(message.Voice?.Content))
                {
                    parts.Add(new GatewayTextContentPart(message.Voice.Content.Trim()));
                    break;
                }

                var voiceMedia = await TryFetchMediaAsync(
                    mediaFetcher,
                    message.Voice?.Url,
                    message.Voice?.AesKey,
                    "voice.amr",
                    cancellationToken).ConfigureAwait(false);

                if (voiceMedia != null)
                {
                    parts.Add(new GatewayAudioContentPart(
                        mediaFetcher.ToDataUrl(voiceMedia),
                        voiceMedia.MimeType));
                    break;
                }

                return (true, CreateParsed(context, parts, "暂不支持纯语音消息，请发送文字。"));
            }

            case "image":
            {
                var imageMedia = await TryFetchMediaAsync(
                    mediaFetcher,
                    message.Image?.Url,
                    message.Image?.AesKey,
                    message.Image?.FileName ?? "image.jpg",
                    cancellationToken).ConfigureAwait(false);

                if (imageMedia == null)
                    return (true, CreateParsed(context, parts, "图片下载或解密失败，请重试。"));

                parts.Add(new GatewayImageContentPart(
                    mediaFetcher.ToDataUrl(imageMedia),
                    imageMedia.MimeType));
                break;
            }

            case "file":
            {
                var fileMedia = await TryFetchMediaAsync(
                    mediaFetcher,
                    message.File?.Url,
                    message.File?.AesKey,
                    message.File?.FileName ?? "file.bin",
                    cancellationToken).ConfigureAwait(false);

                if (fileMedia == null)
                    return (true, CreateParsed(context, parts, "文件下载或解密失败，请重试。"));

                parts.Add(new GatewayFileContentPart(
                    mediaFetcher.ToDataUrl(fileMedia),
                    fileMedia.MimeType,
                    message.File?.FileName));
                break;
            }

            case "video":
            {
                var videoMedia = await TryFetchMediaAsync(
                    mediaFetcher,
                    message.Video?.Url,
                    message.Video?.AesKey,
                    message.Video?.FileName ?? "video.mp4",
                    cancellationToken).ConfigureAwait(false);

                if (videoMedia == null)
                    return (true, CreateParsed(context, parts, "视频下载或解密失败，请重试。"));

                parts.Add(new GatewayFileContentPart(
                    mediaFetcher.ToDataUrl(videoMedia),
                    videoMedia.MimeType,
                    message.Video?.FileName ?? "video.mp4"));
                break;
            }

            default:
                return (true, CreateParsed(context, parts, $"暂不支持「{message.MsgType}」类型消息。"));
        }

        if (parts.Count == 0)
            return (false, null);

        return (true, CreateParsed(context, parts));
    }

    public static GatewayRequest ToGatewayRequest(
        ParsedWeComMessage parsed,
        string sessionId)
    {
        return new GatewayRequest
        {
            SessionId = sessionId,
            UserId = parsed.UserId,
            ChannelId = "wecom",
            Input = parsed.InputParts,
            Stream = true,
            Metadata = new Dictionary<string, object?>
            {
                ["wecom_msg_id"] = parsed.MessageId,
                ["wecom_chat_id"] = parsed.ChatId,
                ["wecom_chat_type"] = parsed.ChatType
            }
        };
    }

    private static IReadOnlyList<GatewayContentPart> BuildTextParts(string text)
        => [new GatewayTextContentPart(text)];

    private static async Task<WeComFetchedMedia?> TryFetchMediaAsync(
        WeComMediaFetcher mediaFetcher,
        string? url,
        string? aesKey,
        string suggestedFileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(aesKey))
            return null;

        return await mediaFetcher.FetchAsync(url, aesKey, suggestedFileName, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ParsedWeComMessage CreateParsed(
        WeComIncomingContext context,
        IReadOnlyList<GatewayContentPart> parts,
        string? unsupportedReply = null)
    {
        var message = context.Message;
        var userId = message.From?.UserId ?? string.Empty;
        var chatId = message.ChatId ?? string.Empty;
        var chatType = message.ChatType ?? "single";
        var msgId = message.MsgId
            ?? $"{userId}_{chatId}_{message.MsgType}_{Guid.NewGuid():N}";

        return new ParsedWeComMessage
        {
            Frame = context.Frame,
            UserId = userId,
            ChatId = chatId,
            ChatType = chatType,
            MessageId = msgId,
            InputParts = parts,
            UnsupportedReply = unsupportedReply
        };
    }
}

public sealed class ParsedWeComMessage
{
    public required WeComWsFrame Frame { get; init; }

    public required string UserId { get; init; }

    public required string ChatId { get; init; }

    public required string ChatType { get; init; }

    public required string MessageId { get; init; }

    public IReadOnlyList<GatewayContentPart> InputParts { get; init; } = [];

    public string? UnsupportedReply { get; init; }

    public bool HasUnsupportedReply => !string.IsNullOrWhiteSpace(UnsupportedReply);

    public string Text =>
        InputParts.OfType<GatewayTextContentPart>().FirstOrDefault()?.Text ?? string.Empty;
}
