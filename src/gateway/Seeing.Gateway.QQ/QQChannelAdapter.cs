using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.QQ.Connection;
using Seeing.Gateway.QQ.Delivery;
using System.Net;
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
        var parsed = Translate(message.Target, _passiveWindow);
        return await SendParsedAsync(parsed, message.Content.Text ?? "", ct, alreadyPrepared: true).ConfigureAwait(false);
    }

    public Task<OutboundDeliveryResult> SendParsedAsync(
        ParsedQQMessage target,
        string text,
        CancellationToken cancellationToken = default) =>
        SendParsedAsync(target, text, cancellationToken, alreadyPrepared: false);

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

    internal static ParsedQQMessage Translate(DeliveryTarget target, QQPassiveWindowTracker tracker) =>
        Prepare(ToParsed(target), tracker);

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

    internal static OutboundFailureKind MapApiFailure(QQApiException exception)
    {
        if ((int)exception.StatusCode == (int)HttpStatusCode.TooManyRequests)
            return OutboundFailureKind.RateLimited;

        return OutboundFailureKind.PlatformError;
    }

    private async Task<OutboundDeliveryResult> SendParsedAsync(
        ParsedQQMessage target,
        string text,
        CancellationToken cancellationToken,
        bool alreadyPrepared)
    {
        var prepared = alreadyPrepared ? target : Prepare(target, _passiveWindow);
        try
        {
            await _http.SendReplyAsync(prepared, text, cancellationToken: cancellationToken).ConfigureAwait(false);
            return OutboundDeliveryResult.Ok();
        }
        catch (QQApiException ex)
        {
            var kind = MapApiFailure(ex);
            _logger.LogWarning(ex, "QQ adapter send failed kind={Kind}", kind);
            return OutboundDeliveryResult.Fail(kind, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QQ adapter send failed");
            return OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, ex.Message);
        }
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
