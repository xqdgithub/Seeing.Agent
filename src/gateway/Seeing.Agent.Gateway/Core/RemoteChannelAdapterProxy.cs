using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Protocol;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 进程外通道的 <see cref="IChannelAdapter"/>。
/// <see cref="OutboundDeliveryResult.Delivered"/> 为 true 表示出站帧已交给当前注册连接，不代表平台已送达。
/// </summary>
public sealed class RemoteChannelAdapterProxy : IChannelAdapter
{
    private readonly Func<GatewayChannelOutboundPayload, bool> _push;

    public RemoteChannelAdapterProxy(string channelId, Func<GatewayChannelOutboundPayload, bool> push)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("channelId is required", nameof(channelId));
        ChannelId = channelId.Trim();
        _push = push ?? throw new ArgumentNullException(nameof(push));
    }

    public string ChannelId { get; }

    public Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        var conversation = message.Target.Conversation;
        var payload = new GatewayChannelOutboundPayload
        {
            Channel = string.IsNullOrWhiteSpace(conversation.ChannelId) ? ChannelId : conversation.ChannelId.Trim(),
            Conversation = conversation,
            RecipientUserId = message.Target.RecipientUserId,
            DeliveryKind = message.DeliveryKind,
            ReplyToMsgId = message.Target.ReplyToMsgId,
            CorrelationSessionId = message.CorrelationSessionId ?? string.Empty,
            Text = message.Text,
            Source = message.Source,
            JobId = message.JobId,
            UserId = message.Target.RecipientUserId ?? conversation.ExternalId
        };

        var handedOff = _push(payload);
        return Task.FromResult(handedOff
            ? OutboundDeliveryResult.Ok()
            : OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, "通道连接不可用"));
    }
}
