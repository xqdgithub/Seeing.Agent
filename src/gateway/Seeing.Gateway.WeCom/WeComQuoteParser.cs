using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 解析企微入站 quote 字段为 <see cref="GatewayQuoteContext"/>
/// </summary>
public static class WeComQuoteParser
{
    public static async Task<GatewayQuoteContext?> ParseAsync(
        WeComQuotePayload? quote,
        WeComMediaFetcher mediaFetcher,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (quote == null)
            return null;

        logger ??= NullLogger.Instance;
        var msgType = quote.MsgType?.ToLowerInvariant() ?? string.Empty;
        var parts = new List<GatewayContentPart>();

        try
        {
            switch (msgType)
            {
                case "text":
                    AddTextPart(parts, quote.Text?.Content);
                    break;

                case "voice":
                    await AddVoicePartsAsync(parts, quote.Voice, mediaFetcher, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "image":
                    await AddImagePartAsync(
                        parts,
                        quote.Image,
                        mediaFetcher,
                        "quote-image.jpg",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "file":
                    await AddFilePartAsync(
                        parts,
                        quote.File,
                        mediaFetcher,
                        quote.File?.FileName ?? "quote-file.bin",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "video":
                    await AddFilePartAsync(
                        parts,
                        quote.Video,
                        mediaFetcher,
                        quote.Video?.FileName ?? "quote-video.mp4",
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "mixed":
                    if (quote.Mixed?.MsgItem != null)
                    {
                        foreach (var item in quote.Mixed.MsgItem)
                        {
                            await AddMixedItemPartsAsync(parts, item, mediaFetcher, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    break;

                default:
                    logger.LogWarning("WeCom quote 未知 msgtype: {MsgType}", quote.MsgType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WeCom quote 解析失败: MsgType={MsgType}", quote.MsgType);
            return null;
        }

        if (parts.Count == 0)
            return null;

        return new GatewayQuoteContext
        {
            MsgType = string.IsNullOrWhiteSpace(msgType) ? "text" : msgType,
            SourceChannel = "wecom",
            Content = parts
        };
    }

    private static async Task AddMixedItemPartsAsync(
        List<GatewayContentPart> parts,
        WeComMixedMessageItem item,
        WeComMediaFetcher mediaFetcher,
        CancellationToken cancellationToken)
    {
        var itemType = item.MsgType?.ToLowerInvariant() ?? string.Empty;
        switch (itemType)
        {
            case "text":
                AddTextPart(parts, item.Text?.Content);
                break;

            case "voice":
                await AddVoicePartsAsync(parts, item.Voice, mediaFetcher, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case "image":
                await AddImagePartAsync(
                    parts,
                    item.Image,
                    mediaFetcher,
                    item.Image?.FileName ?? "quote-image.jpg",
                    cancellationToken).ConfigureAwait(false);
                break;

            case "file":
                await AddFilePartAsync(
                    parts,
                    item.File,
                    mediaFetcher,
                    item.File?.FileName ?? "quote-file.bin",
                    cancellationToken).ConfigureAwait(false);
                break;

            case "video":
                await AddFilePartAsync(
                    parts,
                    item.Video,
                    mediaFetcher,
                    item.Video?.FileName ?? "quote-video.mp4",
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static void AddTextPart(List<GatewayContentPart> parts, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        parts.Add(new GatewayTextContentPart(content.Trim()));
    }

    private static async Task AddVoicePartsAsync(
        List<GatewayContentPart> parts,
        WeComVoicePayload? voice,
        WeComMediaFetcher mediaFetcher,
        CancellationToken cancellationToken)
    {
        if (voice == null)
            return;

        if (!string.IsNullOrWhiteSpace(voice.Content))
        {
            parts.Add(new GatewayTextContentPart(voice.Content.Trim()));
            return;
        }

        var media = await TryFetchMediaAsync(
            mediaFetcher,
            voice.Url,
            voice.AesKey,
            "quote-voice.amr",
            cancellationToken).ConfigureAwait(false);

        if (media != null)
            parts.Add(new GatewayAudioContentPart(mediaFetcher.ToDataUrl(media), media.MimeType));
    }

    private static async Task AddImagePartAsync(
        List<GatewayContentPart> parts,
        WeComEncryptedMediaPayload? image,
        WeComMediaFetcher mediaFetcher,
        string suggestedFileName,
        CancellationToken cancellationToken)
    {
        var media = await TryFetchMediaAsync(
            mediaFetcher,
            image?.Url,
            image?.AesKey,
            suggestedFileName,
            cancellationToken).ConfigureAwait(false);

        if (media == null)
            return;

        parts.Add(new GatewayImageContentPart(mediaFetcher.ToDataUrl(media), media.MimeType));
    }

    private static async Task AddFilePartAsync(
        List<GatewayContentPart> parts,
        WeComEncryptedMediaPayload? file,
        WeComMediaFetcher mediaFetcher,
        string suggestedFileName,
        CancellationToken cancellationToken)
    {
        var media = await TryFetchMediaAsync(
            mediaFetcher,
            file?.Url,
            file?.AesKey,
            suggestedFileName,
            cancellationToken).ConfigureAwait(false);

        if (media == null)
            return;

        parts.Add(new GatewayFileContentPart(
            mediaFetcher.ToDataUrl(media),
            media.MimeType,
            file?.FileName ?? suggestedFileName));
    }

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
}
