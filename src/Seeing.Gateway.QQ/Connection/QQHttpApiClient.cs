using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ OpenAPI HTTP 客户端（文本 fallback + 富媒体出站）。
/// </summary>
public sealed class QQHttpApiClient
{
    public const string HttpClientName = "Seeing.Gateway.QQ";

    public const int MediaTypeImage = 1;
    public const int MediaTypeVideo = 2;
    public const int MediaTypeAudio = 3;
    public const int MediaTypeFile = 4;

    private readonly QQOptions _options;
    private readonly QQAccessTokenProvider _tokenProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QQHttpApiClient> _logger;
    private readonly ConcurrentDictionary<string, int> _msgSeqByKey = new(StringComparer.Ordinal);

    public QQHttpApiClient(
        IOptions<QQOptions> options,
        QQAccessTokenProvider tokenProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<QQHttpApiClient> logger)
    {
        _options = options.Value;
        _tokenProvider = tokenProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 发送最终回复：Composer 提取媒体 → 文本 fallback → C2C/群 upload / 频道图。
    /// </summary>
    public async Task SendReplyAsync(
        ParsedQQMessage target,
        string text,
        object? keyboard = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) && keyboard == null)
            text = "(空回复)";

        var payload = QQOutboundComposer.Compose(text ?? "");
        var mt = target.MessageType.ToLowerInvariant();
        var media = new List<QQOutboundMediaItem>();
        var guildUnsupportedNotes = new List<string>();

        foreach (var item in payload.Media)
        {
            if (mt is "guild" or "dm")
            {
                if (item.Kind == QQOutboundMediaKind.Image)
                    media.Add(item);
                else
                    guildUnsupportedNotes.Add($"[未发送 {item.Kind}: {item.FileName ?? Truncate(item.Source, 80)}]");
            }
            else
            {
                media.Add(item);
            }
        }

        var cleanText = payload.CleanText;
        if (guildUnsupportedNotes.Count > 0)
            cleanText = string.IsNullOrWhiteSpace(cleanText)
                ? string.Join("\n", guildUnsupportedNotes)
                : cleanText + "\n" + string.Join("\n", guildUnsupportedNotes);

        var textSent = false;
        if (!string.IsNullOrWhiteSpace(cleanText) || keyboard != null)
        {
            var bodyText = string.IsNullOrWhiteSpace(cleanText) ? " " : cleanText;
            foreach (var chunk in QQTextSanitizer.SplitChunks(bodyText))
            {
                if (string.IsNullOrWhiteSpace(chunk) && keyboard == null)
                    continue;
                textSent |= await SendTextWithFallbackAsync(target, chunk, keyboard, cancellationToken)
                    .ConfigureAwait(false);
                keyboard = null;
            }
        }

        if (media.Count > 0)
            await SendMediaItemsAsync(target, media, msgId: textSent ? null : target.MsgId, cancellationToken)
                .ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public Task SendTextAsync(
        ParsedQQMessage target,
        string content,
        object? keyboard = null,
        CancellationToken cancellationToken = default) =>
        SendTextWithFallbackAsync(target, content, keyboard, cancellationToken);

    /// <summary>
    /// 发送状态提示（强制明文，不走 Markdown），用于「已收到」等即时反馈。
    /// </summary>
    public Task<bool> SendStatusAsync(
        ParsedQQMessage target,
        string content,
        CancellationToken cancellationToken = default) =>
        SendTextWithFallbackAsync(target, content, keyboard: null, cancellationToken, forcePlaintext: true);

    public async Task<bool> SendTextWithFallbackAsync(
        ParsedQQMessage target,
        string content,
        object? keyboard = null,
        CancellationToken cancellationToken = default,
        bool forcePlaintext = false)
    {
        var useMarkdown = _options.MarkdownEnabled && !forcePlaintext;
        if (!useMarkdown)
        {
            var (sanitized, hadUrl) = QQTextSanitizer.Sanitize(content);
            if (hadUrl)
                _logger.LogInformation("QQ send: stripped URL content for API compatibility");
            content = sanitized;
        }

        try
        {
            await SendTextCoreAsync(target, content, useMarkdown, keyboard, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (QQApiException ex) when (useMarkdown && ex.IsMarkdownValidationError)
        {
            _logger.LogWarning(ex, "QQ markdown send failed; fallback to plain text");
        }
        catch (QQApiException ex) when (!useMarkdown)
        {
            return await TryAggressiveUrlFallbackAsync(target, content, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ send text failed");
            return false;
        }

        var (fallback, hadUrl2) = QQTextSanitizer.Sanitize(content);
        if (hadUrl2)
            _logger.LogInformation("QQ send fallback: stripped URL content");

        try
        {
            await SendTextCoreAsync(target, fallback, useMarkdown: false, keyboard: null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (QQApiException ex2)
        {
            return await TryAggressiveUrlFallbackAsync(target, content, ex2, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ plain text fallback failed");
            return false;
        }
    }

    private async Task<bool> TryAggressiveUrlFallbackAsync(
        ParsedQQMessage target,
        string originalText,
        QQApiException exc,
        CancellationToken cancellationToken)
    {
        if (!exc.IsUrlContentError)
        {
            _logger.LogError(exc, "QQ send text failed");
            return false;
        }

        _logger.LogWarning("QQ rejected URL content; trying aggressive URL stripping");
        var (aggressive, _) = QQTextSanitizer.AggressiveSanitize(originalText);
        try
        {
            await SendTextCoreAsync(target, aggressive, useMarkdown: false, keyboard: null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ aggressive URL fallback failed");
            return false;
        }
    }

    private async Task SendTextCoreAsync(
        ParsedQQMessage target,
        string content,
        bool useMarkdown,
        object? keyboard,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_options.BotPrefix))
            content = _options.BotPrefix + content;

        var body = BuildTextMessageBody(target, content, useMarkdown, keyboard, NextMsgSeq);
        await SendJsonAsync(HttpMethod.Post, ResolveSendPath(target), body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造文本消息体。对齐 QQ/QwenPaw：
    /// C2C/群需要 msg_type+msg_seq；频道/DM 不要；
    /// Markdown 只用 markdown 字段，不要顶层 content。
    /// </summary>
    internal static Dictionary<string, object?> BuildTextMessageBody(
        ParsedQQMessage target,
        string content,
        bool useMarkdown,
        object? keyboard,
        Func<string, int> nextMsgSeq)
    {
        var useMsgSeq = NeedsMsgSeq(target);
        var body = new Dictionary<string, object?>();

        if (useMarkdown)
        {
            body["markdown"] = new { content };
            if (useMsgSeq)
                body["msg_type"] = 2;
        }
        else
        {
            body["content"] = content;
            if (useMsgSeq)
                body["msg_type"] = 0;
        }

        if (useMsgSeq)
            body["msg_seq"] = nextMsgSeq(target.MsgId ?? target.MessageType);

        if (!string.IsNullOrEmpty(target.MsgId))
            body["msg_id"] = target.MsgId;

        if (keyboard != null)
            body["keyboard"] = keyboard;

        return body;
    }

    internal static bool NeedsMsgSeq(ParsedQQMessage target)
    {
        var mt = target.MessageType;
        return mt.Equals("c2c", StringComparison.OrdinalIgnoreCase)
               || mt.Equals("group", StringComparison.OrdinalIgnoreCase);
    }

    private int NextMsgSeq(string key) =>
        _msgSeqByKey.AddOrUpdate(key, 1, static (_, n) => n + 1);

    public async Task SendImagesAsync(
        ParsedQQMessage target,
        IReadOnlyList<string> imageUrls,
        string? msgId,
        CancellationToken cancellationToken = default)
    {
        var items = imageUrls
            .Select(u => new QQOutboundMediaItem(QQOutboundMediaKind.Image, u))
            .ToList();
        await SendMediaItemsAsync(target, items, msgId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendMediaItemsAsync(
        ParsedQQMessage target,
        IReadOnlyList<QQOutboundMediaItem> items,
        string? msgId,
        CancellationToken cancellationToken = default)
    {
        var mt = target.MessageType.ToLowerInvariant();
        if (mt is "guild" or "dm")
        {
            foreach (var item in items.Where(i => i.Kind == QQOutboundMediaKind.Image))
            {
                try
                {
                    if (QQOutboundComposer.IsLocalFile(item.Source))
                        await SendGuildImageFileAsync(target, item.Source, msgId, cancellationToken).ConfigureAwait(false);
                    else if (QQOutboundComposer.IsHttpUrl(item.Source))
                        await SendGuildImageAsync(target, item.Source, msgId, cancellationToken).ConfigureAwait(false);
                    else
                        _logger.LogWarning("Guild image source not supported: {Source}", Truncate(item.Source, 80));
                    msgId = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send guild/dm image: {Source}", item.Source);
                }
            }
            return;
        }

        if (mt is not ("c2c" or "group"))
            return;

        var openId = mt == "c2c" ? target.SenderOpenId : target.GroupOpenId;
        if (string.IsNullOrEmpty(openId))
            return;

        foreach (var item in items)
        {
            try
            {
                var fileType = QQOutboundComposer.ResolveMediaType(item.Kind, item.FileName ?? item.Source);
                var fileInfo = await UploadMediaFromSourceAsync(mt, openId!, fileType, item, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(fileInfo))
                {
                    _logger.LogWarning("Failed to upload media, skipping: {Source}", item.Source);
                    continue;
                }

                await SendMediaMessageAsync(mt, openId!, fileInfo!, msgId, cancellationToken, item.FileName)
                    .ConfigureAwait(false);
                msgId = null;
                _logger.LogInformation("Successfully sent media {Kind}: {Source}", item.Kind, Truncate(item.Source, 120));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send media: {Source}", item.Source);
            }
        }
    }

    internal static Dictionary<string, object?> BuildUploadMediaBody(
        int fileType,
        string? url,
        string? fileData,
        string? fileName)
    {
        var body = new Dictionary<string, object?>
        {
            ["file_type"] = fileType,
            ["srv_send_msg"] = false
        };
        if (!string.IsNullOrEmpty(fileData))
        {
            body["file_data"] = fileData;
            if (!string.IsNullOrEmpty(fileName))
                body["file_name"] = fileName;
        }
        else if (!string.IsNullOrEmpty(url))
        {
            body["url"] = url;
        }
        else
        {
            throw new ArgumentException("Either url or fileData is required");
        }

        return body;
    }

    public async Task<string?> UploadMediaAsync(
        string messageType,
        string openId,
        int mediaType,
        string url,
        CancellationToken cancellationToken = default) =>
        await UploadMediaFromSourceAsync(
            messageType,
            openId,
            mediaType,
            new QQOutboundMediaItem(QQOutboundMediaKind.Image, url),
            cancellationToken).ConfigureAwait(false);

    private async Task<string?> UploadMediaFromSourceAsync(
        string messageType,
        string openId,
        int mediaType,
        QQOutboundMediaItem item,
        CancellationToken cancellationToken)
    {
        var path = ResolveMediaPath(messageType, openId, "files");
        if (path == null)
            return null;

        try
        {
            Dictionary<string, object?> body;
            if (QQOutboundComposer.IsHttpUrl(item.Source))
            {
                body = BuildUploadMediaBody(mediaType, item.Source, null, item.FileName);
            }
            else
            {
                var (data, name) = await ReadSourceAsBase64Async(item, cancellationToken).ConfigureAwait(false);
                if (data == null)
                    return null;
                body = BuildUploadMediaBody(mediaType, null, data, name ?? item.FileName);
            }

            var responseBody = await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody);
            if (doc.RootElement.TryGetProperty("file_info", out var fi) && fi.ValueKind == JsonValueKind.String)
                return fi.GetString();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upload media: {Source}", Truncate(item.Source, 120));
            return null;
        }
    }

    private async Task<(string? Data, string? FileName)> ReadSourceAsBase64Async(
        QQOutboundMediaItem item,
        CancellationToken cancellationToken)
    {
        if (QQOutboundComposer.IsDataUrl(item.Source))
        {
            var comma = item.Source.IndexOf(',');
            if (comma < 0)
                return (null, null);
            var meta = item.Source[..comma];
            var b64 = item.Source[(comma + 1)..];
            string? name = item.FileName;
            if (name == null && meta.Contains("image/png", StringComparison.OrdinalIgnoreCase))
                name = "image.png";
            return (b64, name);
        }

        if (!QQOutboundComposer.IsLocalFile(item.Source))
        {
            _logger.LogWarning("Media source not found: {Source}", item.Source);
            return (null, null);
        }

        var bytes = await File.ReadAllBytesAsync(item.Source, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > _options.MaxMediaBytes)
        {
            _logger.LogWarning("Media too large for upload: {Bytes}", bytes.Length);
            return (null, null);
        }

        return (Convert.ToBase64String(bytes), item.FileName ?? Path.GetFileName(item.Source));
    }

    public async Task SendMediaMessageAsync(
        string messageType,
        string openId,
        string fileInfo,
        string? msgId,
        CancellationToken cancellationToken = default,
        string? filename = null)
    {
        var path = ResolveMediaPath(messageType, openId, "messages");
        if (path == null)
            return;

        var body = new Dictionary<string, object?>
        {
            ["msg_type"] = 7,
            ["media"] = new { file_info = fileInfo },
            ["msg_seq"] = NextMsgSeq(msgId ?? $"{messageType}_media")
        };
        if (!string.IsNullOrEmpty(filename))
            body["content"] = filename;
        if (!string.IsNullOrEmpty(msgId))
            body["msg_id"] = msgId;

        await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendGuildImageAsync(
        ParsedQQMessage target,
        string imageUrl,
        string? msgId,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["image"] = imageUrl };
        if (!string.IsNullOrEmpty(msgId))
            body["msg_id"] = msgId;

        await SendJsonAsync(HttpMethod.Post, ResolveSendPath(target), body, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendGuildImageFileAsync(
        ParsedQQMessage target,
        string filePath,
        string? msgId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Guild image file not found", filePath);

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > _options.MaxMediaBytes)
            throw new InvalidOperationException($"Guild image too large: {bytes.Length} bytes");

        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(_options.ApiBase.TrimEnd('/') + "/");

        var path = ResolveSendPath(target).TrimStart('/');
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrEmpty(msgId))
            content.Add(new StringContent(msgId), "msg_id");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file_image", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Headers.TryAddWithoutValidation("X-Union-Appid", _options.AppId);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("QQ guild file_image failed: {Status} {Body}", response.StatusCode, responseBody);
            throw new QQApiException(response.StatusCode, path, responseBody);
        }
    }

    public async Task AckInteractionAsync(string interactionId, int code = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interactionId))
            return;

        await SendJsonAsync(
            HttpMethod.Put,
            $"/interactions/{interactionId}",
            new { code },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetGatewayUrlAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(_options.ApiBase.TrimEnd('/') + "/");

        using var request = new HttpRequestMessage(HttpMethod.Get, "gateway");
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Headers.TryAddWithoutValidation("X-Union-Appid", _options.AppId);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("QQ gateway url missing");
    }

    private static string? ResolveMediaPath(string messageType, string openId, string suffix) =>
        messageType.ToLowerInvariant() switch
        {
            "c2c" => $"/v2/users/{openId}/{suffix}",
            "group" => $"/v2/groups/{openId}/{suffix}",
            _ => null
        };

    private string ResolveSendPath(ParsedQQMessage target) =>
        target.MessageType.ToLowerInvariant() switch
        {
            // 禁止 group/guild/dm 静默回退到 C2C，否则群里看起来像「没收到」
            "group" => !string.IsNullOrEmpty(target.GroupOpenId)
                ? $"/v2/groups/{target.GroupOpenId}/messages"
                : throw new InvalidOperationException("QQ group message missing group_openid"),
            "guild" => !string.IsNullOrEmpty(target.ChannelId)
                ? $"/channels/{target.ChannelId}/messages"
                : throw new InvalidOperationException("QQ guild message missing channel_id"),
            "dm" => !string.IsNullOrEmpty(target.GuildId)
                ? $"/dms/{target.GuildId}/messages"
                : throw new InvalidOperationException("QQ dm message missing guild_id"),
            "c2c" when !string.IsNullOrEmpty(target.SenderOpenId) =>
                $"/v2/users/{target.SenderOpenId}/messages",
            _ when !string.IsNullOrEmpty(target.SenderOpenId) =>
                $"/v2/users/{target.SenderOpenId}/messages",
            _ => throw new InvalidOperationException($"Cannot resolve QQ send path for {target.MessageType}")
        };

    private async Task<string> SendJsonAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(_options.ApiBase.TrimEnd('/') + "/");

        using var request = new HttpRequestMessage(method, path.TrimStart('/'))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        // 请求级 Header，避免并发修改 HttpClient.DefaultRequestHeaders
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Headers.TryAddWithoutValidation("X-Union-Appid", _options.AppId);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("QQ API {Method} {Path} failed: {Status} {Body}", method, path, response.StatusCode, responseBody);
            throw new QQApiException(response.StatusCode, path, responseBody);
        }

        return responseBody;
    }
}
