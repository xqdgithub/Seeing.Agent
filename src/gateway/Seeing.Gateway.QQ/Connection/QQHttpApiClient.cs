using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Seeing.Gateway.QQ.Connection;

/// <summary>
/// QQ OpenAPI HTTP 客户端（文本 + 结构化附件出站）。
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
    /// 发送单个结构化附件：c2c/群 走「上传 → <c>msg_type=7</c>」，频道/DM 仅接受图片文件。
    /// <c>msg_id</c> 只取 <paramref name="target"/>.MsgId（由适配器逐条 acquire 被动额度后写入）。
    /// </summary>
    public async Task<OutboundDeliveryResult> SendAttachmentAsync(
        ParsedQQMessage target,
        OutboundAttachment attachment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(attachment);

        try
        {
            var messageType = target.MessageType.ToLowerInvariant();
            if (messageType is "guild" or "dm")
            {
                // 频道/DM 只支持图片；在读取文件前原子拒绝
                if (attachment.Kind != OutboundAttachmentKind.Image)
                {
                    return OutboundDeliveryResult.Fail(
                        OutboundFailureKind.AttachmentUnsupported,
                        $"频道/DM 仅支持图片附件，收到 {attachment.Kind}");
                }

                if (ValidateLocalFile(attachment.LocalPath) is { } invalidGuild)
                    return invalidGuild;

                await SendGuildImageFileAsync(target, attachment.LocalPath, ct).ConfigureAwait(false);
                return OutboundDeliveryResult.Ok();
            }

            if (messageType is not ("c2c" or "group"))
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentUnsupported,
                    $"不支持的消息类型 {target.MessageType}");
            }

            var openId = messageType == "c2c" ? target.SenderOpenId : target.GroupOpenId;
            if (string.IsNullOrEmpty(openId))
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentUnsupported,
                    "目标会话缺少 openid");
            }

            // 读取前预检：避免把缺失/超限文件读入内存
            if (ValidateLocalFile(attachment.LocalPath) is { } invalid)
                return invalid;

            var fileName = attachment.FileName ?? Path.GetFileName(attachment.LocalPath);
            var mediaType = ResolveMediaType(attachment.Kind, fileName);
            var bytes = await File.ReadAllBytesAsync(attachment.LocalPath, ct).ConfigureAwait(false);
            // 读取期安全断言：预检后文件可能被替换/增长（TOCTOU）
            if (bytes.Length > _options.MaxMediaBytes)
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentTooLarge,
                    $"{bytes.Length} 字节超出上限 {_options.MaxMediaBytes} 字节");
            }

            var uploadPath = ResolveMediaPath(messageType, openId, "files")
                ?? throw new InvalidOperationException($"Cannot resolve QQ upload path for {target.MessageType}");
            var uploadBody = BuildUploadMediaBody(
                mediaType, url: null, fileData: Convert.ToBase64String(bytes), fileName);
            var uploadResponse = await SendJsonAsync(HttpMethod.Post, uploadPath, uploadBody, ct)
                .ConfigureAwait(false);

            var fileInfo = ParseFileInfo(uploadResponse);
            if (string.IsNullOrEmpty(fileInfo))
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.PlatformError, "上传响应缺少 file_info");
            }

            await SendMediaMessageAsync(messageType, openId, fileInfo, target.MsgId, ct, fileName)
                .ConfigureAwait(false);
            return OutboundDeliveryResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消必须传播
            throw;
        }
        catch (QQApiException ex)
        {
            ct.ThrowIfCancellationRequested();
            var kind = QQFailureMapper.MapApiFailure(ex);
            _logger.LogWarning(ex, "QQ attachment send failed kind={Kind}", kind);
            return OutboundDeliveryResult.Fail(kind, ex.Message);
        }
        catch (IOException ex)
        {
            ct.ThrowIfCancellationRequested();
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            ct.ThrowIfCancellationRequested();
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, ex.Message);
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "QQ attachment send failed");
            return OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, ex.Message);
        }
    }

    /// <summary>本地文件预检：不存在/不可读/空文件 → <see cref="OutboundFailureKind.AttachmentUnavailable"/>；超限 → <see cref="OutboundFailureKind.AttachmentTooLarge"/>。</summary>
    private OutboundDeliveryResult? ValidateLocalFile(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, "文件不存在或不可读");
        if (fileInfo.Length == 0)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, "文件为空");
        if (fileInfo.Length > _options.MaxMediaBytes)
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentTooLarge,
                $"{fileInfo.Length} 字节超出上限 {_options.MaxMediaBytes} 字节");
        }

        return null;
    }

    private static string? ParseFileInfo(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.TryGetProperty("file_info", out var fileInfo)
               && fileInfo.ValueKind == JsonValueKind.String
            ? fileInfo.GetString()
            : null;
    }

    private static readonly HashSet<string> s_voiceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".amr", ".silk"
    };

    /// <summary>结构化附件种类 → QQ <c>file_type</c>；语音扩展名（.silk/.amr）强制为 3。</summary>
    internal static int ResolveMediaType(OutboundAttachmentKind kind, string? fileName)
    {
        if (s_voiceExtensions.Contains(Path.GetExtension(fileName ?? "")))
            return MediaTypeAudio;

        return kind switch
        {
            OutboundAttachmentKind.Image => MediaTypeImage,
            OutboundAttachmentKind.Video => MediaTypeVideo,
            OutboundAttachmentKind.Audio => MediaTypeAudio,
            _ => MediaTypeFile
        };
    }

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
            await TryAggressiveUrlFallbackAsync(target, content, ex, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ send text failed");
            throw;
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
            await TryAggressiveUrlFallbackAsync(target, content, ex2, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ plain text fallback failed");
            throw;
        }
    }

    private async Task TryAggressiveUrlFallbackAsync(
        ParsedQQMessage target,
        string originalText,
        QQApiException exc,
        CancellationToken cancellationToken)
    {
        if (!exc.IsUrlContentError)
        {
            _logger.LogError(exc, "QQ send text failed");
            throw exc;
        }

        _logger.LogWarning("QQ rejected URL content; trying aggressive URL stripping");
        var (aggressive, _) = QQTextSanitizer.AggressiveSanitize(originalText);
        try
        {
            await SendTextCoreAsync(target, aggressive, useMarkdown: false, keyboard: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ aggressive URL fallback failed");
            throw;
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
            body["msg_seq"] = nextMsgSeq(ResolveConversationSeqKey(target));

        // 主动模式（无被动窗口 msg_id）不带 msg_id。
        if (!string.IsNullOrEmpty(target.MsgId))
            body["msg_id"] = target.MsgId;

        if (keyboard != null)
            body["keyboard"] = keyboard;

        return body;
    }

    /// <summary>msg_seq 按会话键递增：<c>{messageType}:{openId}</c>。</summary>
    internal static string ResolveConversationSeqKey(ParsedQQMessage target)
    {
        var mt = target.MessageType.ToLowerInvariant();
        var openId = mt switch
        {
            "group" => target.GroupOpenId,
            "guild" => target.ChannelId,
            _ => target.SenderOpenId
        };
        return $"{mt}:{openId}";
    }

    internal static bool NeedsMsgSeq(ParsedQQMessage target)
    {
        var mt = target.MessageType;
        return mt.Equals("c2c", StringComparison.OrdinalIgnoreCase)
               || mt.Equals("group", StringComparison.OrdinalIgnoreCase);
    }

    private int NextMsgSeq(string key) =>
        _msgSeqByKey.AddOrUpdate(key, 1, static (_, n) => n + 1);

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
            ["msg_seq"] = NextMsgSeq($"{messageType.ToLowerInvariant()}:{openId}")
        };
        if (!string.IsNullOrEmpty(filename))
            body["content"] = filename;
        if (!string.IsNullOrEmpty(msgId))
            body["msg_id"] = msgId;

        await SendJsonAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>频道/DM 图片文件上传；<c>msg_id</c> 取自 <paramref name="target"/>.MsgId。</summary>
    public async Task SendGuildImageFileAsync(
        ParsedQQMessage target,
        string filePath,
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
        if (!string.IsNullOrEmpty(target.MsgId))
            content.Add(new StringContent(target.MsgId), "msg_id");
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

    internal static string ResolveSendPath(ParsedQQMessage target) =>
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
