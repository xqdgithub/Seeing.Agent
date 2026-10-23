using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.QQ.Connection;
using Seeing.Gateway.QQ.Delivery;
using System.Text.Json;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ 通道适配器：入站事件翻译为 <see cref="ChannelInboundMessage"/>，出站 <see cref="DeliveryTarget"/> 翻译为 OpenAPI。
/// </summary>
public sealed class QQChannelAdapter : IChannelAdapter
{
    public const string Channel = "qq";

    private readonly QQHttpApiClient _http;
    private readonly QQPassiveWindowTracker _passiveWindow;
    private readonly IChannelDirectory? _directory;
    private readonly ILogger<QQChannelAdapter> _logger;
    private readonly QQCommandInterceptor _commands = new();
    private readonly ChannelCapabilities _capabilities;

    public QQChannelAdapter(
        QQHttpApiClient http,
        QQPassiveWindowTracker passiveWindow,
        IOptions<QQOptions> options,
        ILogger<QQChannelAdapter> logger,
        IChannelDirectory? directory = null)
    {
        _http = http;
        _passiveWindow = passiveWindow;
        _logger = logger;
        _directory = directory;

        // 能力构造时固化：Guild 频道只接受图片，其余对话四类附件共用同一大小上限
        var maxMediaBytes = options.Value.MaxMediaBytes;
        _capabilities = new ChannelCapabilities
        {
            SupportsReplyAnchor = true,
            Attachments = new()
            {
                Kinds =
                [
                    OutboundAttachmentKind.Image,
                    OutboundAttachmentKind.Video,
                    OutboundAttachmentKind.Audio,
                    OutboundAttachmentKind.File,
                ],
                MaxBytes = maxMediaBytes,
                MaxPerMessage = 5,
            },
            AttachmentsByConversationKind =
            [
                new()
                {
                    Kind = ConversationKind.Guild,
                    Capabilities = new()
                    {
                        Kinds = [OutboundAttachmentKind.Image],
                        MaxBytes = maxMediaBytes,
                        MaxPerMessage = 5,
                    },
                },
            ],
        };
    }

    public string ChannelId => Channel;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => _capabilities;

    public bool HasDirectory => _directory != null;

    public void RegisterDirectoryInbound(ChannelInboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _directory?.RegisterInbound(message);
    }

    public Task SetOptOutAsync(string externalId, bool optedOut, CancellationToken cancellationToken = default)
    {
        if (_directory == null || string.IsNullOrWhiteSpace(externalId))
            return Task.CompletedTask;

        return _directory.SetOptOutAsync(ChannelId, externalId, optedOut, cancellationToken);
    }

    public void RegisterInbound(string msgId, DateTimeOffset at) =>
        _passiveWindow.RegisterInbound(msgId, at);

    public async Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return await SendContentAsync(ToParsed(message.Target), message.Content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送纯文本终态回复。空文本视为已处理、不发送（桥的 <c>SendVisibleAsync</c> 已在上层兜底「(空回复)」）。
    /// </summary>
    public Task<OutboundDeliveryResult> SendParsedAsync(
        ParsedQQMessage target,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(OutboundDeliveryResult.Ok());

        return SendContentAsync(target, new OutboundContent { Text = text }, cancellationToken);
    }

    public ParsedQQMessage ApplyPassiveWindow(ParsedQQMessage target) =>
        Prepare(target, _passiveWindow);

    public bool TryMapInbound(
        string eventType,
        JsonElement payload,
        QQOptions options,
        out ChannelInboundMessage? message) =>
        TryMapInbound(eventType, payload, options, _commands, out message);

    internal static bool TryMapInbound(
        string eventType,
        JsonElement payload,
        QQOptions options,
        QQCommandInterceptor commands,
        out ChannelInboundMessage? message)
    {
        message = null;
        if (!QQMessageParser.TryParse(eventType, payload, out var parsed) || parsed == null)
            return false;

        var sessionId = QQSessionResolver.ResolveSessionId(parsed, options);
        var kind = commands.TryParse(parsed.Text, out _);
        message = new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity
            {
                ChannelId = Channel,
                ExternalId = string.IsNullOrWhiteSpace(parsed.SenderOpenId) ? "unknown" : parsed.SenderOpenId
            },
            Conversation = ToConversation(parsed),
            ReplyToMsgId = parsed.MsgId,
            SessionId = sessionId,
            Text = parsed.Text,
            CommandToken = QQCommandInterceptor.ToCommandToken(kind)
        };
        return true;
    }

    internal static ParsedQQMessage ToParsed(DeliveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var conversation = target.Conversation;
        var msgId = target.ReplyToMsgId ?? "";
        return conversation.Kind switch
        {
            ConversationKind.Direct => new ParsedQQMessage
            {
                MessageType = "c2c",
                MsgId = msgId,
                SenderOpenId = FirstNonEmpty(conversation.ExternalId, target.RecipientUserId),
                Text = ""
            },
            ConversationKind.Group => new ParsedQQMessage
            {
                MessageType = "group",
                MsgId = msgId,
                GroupOpenId = conversation.ExternalId,
                SenderOpenId = target.RecipientUserId,
                Text = ""
            },
            ConversationKind.Guild => new ParsedQQMessage
            {
                MessageType = "guild",
                MsgId = msgId,
                ChannelId = conversation.ExternalId,
                Text = ""
            },
            _ => throw new InvalidOperationException($"Unsupported conversation kind {conversation.Kind}")
        };
    }

    /// <summary>
    /// 出站编排：文本逐块、附件逐项，各自独立消耗被动额度后发送。
    /// 全部成功返回 <see cref="OutboundDeliveryResult.Ok"/>；部分失败聚合为 <see cref="OutboundFailureKind.PlatformError"/>。
    /// </summary>
    private async Task<OutboundDeliveryResult> SendContentAsync(
        ParsedQQMessage target,
        OutboundContent content,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var failures = new List<(string? Label, OutboundDeliveryResult Result)>();
        var total = 0;
        var sent = 0;

        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            foreach (var chunk in QQTextSanitizer.SplitChunks(content.Text))
            {
                if (string.IsNullOrWhiteSpace(chunk))
                    continue;

                ct.ThrowIfCancellationRequested();
                total++;
                var chunkResult = await SendTextChunkAsync(target, chunk, ct).ConfigureAwait(false);
                if (chunkResult.Delivered)
                    sent++;
                else
                    failures.Add((null, chunkResult));
            }
        }

        foreach (var attachment in content.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            total++;
            var attachmentResult = await SendAttachmentItemAsync(target, attachment, ct).ConfigureAwait(false);
            if (attachmentResult.Delivered)
                sent++;
            else
                failures.Add((attachment.FileName ?? attachment.LocalPath, attachmentResult));
        }

        if (failures.Count == 0)
            return OutboundDeliveryResult.Ok();

        // 一条都没送达：原样返回首个失败结果
        if (sent == 0)
            return failures[0].Result;

        var detail = string.Join(
            "；",
            failures.Select(f => f.Label == null
                ? f.Result.Detail ?? "未知错误"
                : $"{f.Label}: {f.Result.Detail ?? "未知错误"}"));
        return OutboundDeliveryResult.Fail(
            OutboundFailureKind.PlatformError,
            $"已发送 {sent}/{total}；失败项：{detail}");
    }

    private async Task<OutboundDeliveryResult> SendTextChunkAsync(
        ParsedQQMessage target,
        string chunk,
        CancellationToken ct)
    {
        var prepared = ApplyPassiveWindow(target);
        try
        {
            await _http.SendTextAsync(prepared, chunk, keyboard: null, cancellationToken: ct)
                .ConfigureAwait(false);
            return OutboundDeliveryResult.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消必须传播且停止后续项
            throw;
        }
        catch (QQApiException ex)
        {
            ct.ThrowIfCancellationRequested();
            var kind = QQFailureMapper.MapApiFailure(ex);
            _logger.LogWarning(ex, "QQ adapter send text failed kind={Kind}", kind);
            return OutboundDeliveryResult.Fail(kind, ex.Message);
        }
        catch (Exception ex)
        {
            // 取消期间抛出的非 OCE 异常统一归一为取消；超时等非用户取消的 OCE 走失败聚合
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "QQ adapter send text failed");
            return OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, ex.Message);
        }
    }

    private async Task<OutboundDeliveryResult> SendAttachmentItemAsync(
        ParsedQQMessage target,
        OutboundAttachment attachment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        // 能力解析：按目标对话种类覆盖 → 回落默认；null 表示该通道不支持附件
        var conversationKind = ToConversation(target).Kind;
        var capabilities = _capabilities.ResolveAttachments(conversationKind);
        if (capabilities == null)
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentUnsupported, "通道不支持附件");
        }

        if (!capabilities.Supports(attachment.Kind))
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentUnsupported, $"不支持 {attachment.Kind} 附件");
        }

        // 能力预检在适配器完成：读取文件前按目标对话种类拒绝缺失/超限
        var file = new FileInfo(attachment.LocalPath);
        if (!file.Exists)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, "文件不存在或不可读");
        if (file.Length == 0)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.AttachmentUnavailable, "文件为空");
        if (file.Length > capabilities.MaxBytes)
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentTooLarge,
                $"{file.Length} 字节超出上限 {capabilities.MaxBytes} 字节");
        }

        var prepared = ApplyPassiveWindow(target);
        return await _http.SendAttachmentAsync(prepared, attachment, ct).ConfigureAwait(false);
    }

    private static ParsedQQMessage Prepare(ParsedQQMessage target, QQPassiveWindowTracker tracker)
    {
        if (string.IsNullOrEmpty(target.MsgId))
            return target;

        var decision = tracker.TryAcquirePassive(target.MsgId);
        if (!decision.MustDropMsgId)
            return target;

        return Clone(target, msgId: "");
    }

    internal static ConversationInfo ToConversation(ParsedQQMessage parsed)
    {
        if (string.Equals(parsed.MessageType, "group", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversationInfo
            {
                ChannelId = Channel,
                Kind = ConversationKind.Group,
                ExternalId = parsed.GroupOpenId ?? ""
            };
        }

        if (string.Equals(parsed.MessageType, "guild", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversationInfo
            {
                ChannelId = Channel,
                Kind = ConversationKind.Guild,
                ExternalId = parsed.ChannelId ?? ""
            };
        }

        return new ConversationInfo
        {
            ChannelId = Channel,
            Kind = ConversationKind.Direct,
            ExternalId = parsed.SenderOpenId ?? ""
        };
    }

    private static ParsedQQMessage Clone(ParsedQQMessage source, string msgId) => new()
    {
        MessageType = source.MessageType,
        MsgId = msgId,
        SenderOpenId = source.SenderOpenId,
        GroupOpenId = source.GroupOpenId,
        ChannelId = source.ChannelId,
        GuildId = source.GuildId,
        Text = source.Text,
        Attachments = source.Attachments,
        IsBotAuthor = source.IsBotAuthor,
        Raw = source.Raw
    };

    private static string? FirstNonEmpty(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
}
