using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.WeCom.Connection;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微通道适配器：入站事件翻译为 <see cref="ChannelInboundMessage"/>，出站 <see cref="DeliveryTarget"/> 翻译为长连接出站参数。
/// 流式增量仍由 <see cref="WeComOutboundChannel.ReplyStreamAsync"/> 在 <c>finish=false</c> 时旁路直发；终态经本适配器。
/// </summary>
public sealed class WeComChannelAdapter : IChannelAdapter
{
    public const string Channel = "wecom";

    private readonly WeComOutboundChannel _outbound;
    private readonly WeComAibotWsClient _client;
    private readonly IChannelDirectory? _directory;
    private readonly ILogger<WeComChannelAdapter> _logger;
    private readonly ConcurrentDictionary<string, WeComWsFrame> _inboundFrames = new(StringComparer.Ordinal);

    public WeComChannelAdapter(
        WeComOutboundChannel outbound,
        WeComAibotWsClient client,
        ILogger<WeComChannelAdapter> logger,
        IChannelDirectory? directory = null)
    {
        _outbound = outbound;
        _client = client;
        _logger = logger;
        _directory = directory;
        _outbound.BindTerminalDelivery(DeliverTerminalAsync);
    }

    public string ChannelId => Channel;

    /// <summary>企微一期不声明附件能力，也不声明回复锚点（入站帧无失效判定，见设计 §10）。</summary>
    public ChannelCapabilities Capabilities => new() { SupportsReplyAnchor = false };

    public bool HasDirectory => _directory != null;

    public void RegisterDirectoryInbound(ChannelInboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _directory?.RegisterInbound(message);
    }

    public void RememberInbound(ParsedWeComMessage parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (!string.IsNullOrWhiteSpace(parsed.MessageId))
            _inboundFrames[parsed.MessageId] = parsed.Frame;
    }

    public Task SetOptOutAsync(string externalId, bool optedOut, CancellationToken cancellationToken = default)
    {
        if (_directory == null || string.IsNullOrWhiteSpace(externalId))
            return Task.CompletedTask;

        return _directory.SetOptOutAsync(ChannelId, externalId, optedOut, cancellationToken);
    }

    public async Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.Content.Text ?? "";
        var body = ToOutboundBody(message.Target, text);
        try
        {
            if (!string.IsNullOrEmpty(body.MsgId) && _inboundFrames.TryGetValue(body.MsgId, out var frame))
            {
                await DeliverTerminalAsync(
                    frame,
                    WeComAibotWsClient.GenerateStreamId(),
                    text,
                    _client.ConnectionEpoch,
                    ct).ConfigureAwait(false);
                return OutboundDeliveryResult.Ok();
            }

            var command = string.IsNullOrEmpty(body.MsgId)
                ? WeComWsCommands.SendMsg
                : WeComWsCommands.RespondMsg;
            await _outbound.SendCommandAsync(
                command,
                WeComConnectionManager.GenerateReqId(),
                body,
                _client.ConnectionEpoch,
                ct,
                WeComOutboundPriority.Finish).ConfigureAwait(false);
            return OutboundDeliveryResult.Ok();
        }
        catch (WeComConnectionEpochException ex)
        {
            _logger.LogWarning(ex, "WeCom adapter send failed: connection epoch");
            return OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeCom adapter send failed");
            return OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, ex.Message);
        }
    }

    internal static bool TryMapInbound(
        ParsedWeComMessage parsed,
        WeComOptions options,
        out ChannelInboundMessage? message)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(options);

        string? commandToken = null;
        if (WeComCommandInterceptor.TryParseCommand(parsed, out var command))
            commandToken = WeComCommandInterceptor.ToCommandToken(command);

        message = new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity
            {
                ChannelId = Channel,
                ExternalId = string.IsNullOrWhiteSpace(parsed.UserId) ? "unknown" : parsed.UserId
            },
            Conversation = ToConversation(parsed),
            ReplyToMsgId = parsed.MessageId,
            SessionId = WeComSessionResolver.ResolveSessionId(parsed, options),
            Text = parsed.Text,
            CommandToken = commandToken
        };
        return true;
    }

    internal static bool TryMapTextEvent(
        WeComIncomingContext context,
        WeComOptions options,
        out ChannelInboundMessage? message)
    {
        if (!WeComMessageParser.TryParseText(context, out var parsed) || parsed == null)
        {
            message = null;
            return false;
        }

        return TryMapInbound(parsed, options, out message);
    }

    internal static WeComOutboundParameters Translate(DeliveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var conversation = target.Conversation;
        return conversation.Kind switch
        {
            ConversationKind.Direct => new WeComOutboundParameters
            {
                ChatType = "single",
                UserId = FirstNonEmpty(conversation.ExternalId, target.RecipientUserId) ?? "",
                ChatId = "",
                MsgId = target.ReplyToMsgId
            },
            ConversationKind.Group => new WeComOutboundParameters
            {
                ChatType = "group",
                ChatId = conversation.ExternalId ?? "",
                UserId = target.RecipientUserId,
                MsgId = target.ReplyToMsgId
            },
            _ => throw new InvalidOperationException($"Unsupported conversation kind {conversation.Kind}")
        };
    }

    internal static WeComMappedOutboundBody ToOutboundBody(DeliveryTarget target, string text)
    {
        var parameters = Translate(target);
        return new WeComMappedOutboundBody
        {
            ChatType = parameters.ChatType,
            ChatId = string.IsNullOrEmpty(parameters.ChatId) ? null : parameters.ChatId,
            UserId = string.IsNullOrEmpty(parameters.UserId) ? null : parameters.UserId,
            MsgId = parameters.MsgId,
            Text = new WeComTextPayload { Content = text }
        };
    }

    internal static ConversationInfo ToConversation(ParsedWeComMessage parsed)
    {
        if (string.Equals(parsed.ChatType, "group", StringComparison.OrdinalIgnoreCase))
        {
            return new ConversationInfo
            {
                ChannelId = Channel,
                Kind = ConversationKind.Group,
                ExternalId = parsed.ChatId ?? ""
            };
        }

        return new ConversationInfo
        {
            ChannelId = Channel,
            Kind = ConversationKind.Direct,
            ExternalId = string.IsNullOrWhiteSpace(parsed.UserId) ? parsed.ChatId ?? "" : parsed.UserId
        };
    }

    private Task DeliverTerminalAsync(
        WeComWsFrame requestFrame,
        string streamId,
        string content,
        long epoch,
        CancellationToken cancellationToken)
    {
        var body = new WeComRespondStreamBody
        {
            Stream = new WeComStreamPayload
            {
                Id = streamId,
                Content = content,
                Finish = true
            }
        };

        return _outbound.SendCommandAsync(
            WeComWsCommands.RespondMsg,
            requestFrame.Headers?.ReqId,
            body,
            epoch,
            cancellationToken,
            WeComOutboundPriority.Finish);
    }

    private static string? FirstNonEmpty(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
}

internal sealed record WeComOutboundParameters
{
    public required string ChatType { get; init; }

    public string? ChatId { get; init; }

    public string? UserId { get; init; }

    public string? MsgId { get; init; }
}

/// <summary>DeliveryTarget 翻译后的企微出站 body（主动 <c>aibot_send_msg</c> / 被动 <c>aibot_respond_msg</c>）。</summary>
public sealed class WeComMappedOutboundBody
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; init; } = "text";

    [JsonPropertyName("chattype")]
    public string? ChatType { get; init; }

    [JsonPropertyName("chatid")]
    public string? ChatId { get; init; }

    [JsonPropertyName("userid")]
    public string? UserId { get; init; }

    [JsonPropertyName("msgid")]
    public string? MsgId { get; init; }

    [JsonPropertyName("text")]
    public WeComTextPayload? Text { get; init; }
}
