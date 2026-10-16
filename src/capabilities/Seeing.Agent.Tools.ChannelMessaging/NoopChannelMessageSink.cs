using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 未组合通联模块时的缺省出口。不投递，向调用方说明通道未配置。
/// </summary>
public sealed class NoopChannelMessageSink : IChannelMessageSink
{
    public const string UnavailableDetail = "通道投递未配置";

    public Task<OutboundDeliveryResult> SendToUserAsync(
        string channelId, string userExternalId, string text, CancellationToken ct = default)
        => Task.FromResult(OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, UnavailableDetail));

    public Task<OutboundDeliveryResult> SendToConversationAsync(
        DeliveryTarget target, string text, CancellationToken ct = default)
        => Task.FromResult(OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, UnavailableDetail));
}
