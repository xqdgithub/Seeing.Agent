using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>Agent 工具的单向出站出口，薄封装投递管道。</summary>
public sealed class ChannelMessageSink : IChannelMessageSink
{
    private readonly IChannelDirectory _directory;
    private readonly OutboundDeliveryService _delivery;

    public ChannelMessageSink(IChannelDirectory directory, OutboundDeliveryService delivery)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public async Task<OutboundDeliveryResult> SendToUserAsync(
        string channelId, string userExternalId, string text, CancellationToken ct = default)
    {
        var user = await _directory.FindUserAsync(channelId, userExternalId, ct).ConfigureAwait(false);
        var message = new OutboundMessage
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = ConversationKind.Direct,
                    ExternalId = userExternalId,
                    Title = user?.DisplayName,
                },
            },
            DeliveryKind = OutboundDeliveryKind.Push,
            Text = text,
            Source = "tool.send_message",
        };
        return await _delivery.DeliverAsync(message, ct).ConfigureAwait(false);
    }

    public Task<OutboundDeliveryResult> SendToConversationAsync(
        DeliveryTarget target, string text, CancellationToken ct = default)
    {
        var message = new OutboundMessage
        {
            Target = target,
            DeliveryKind = OutboundDeliveryKind.Push,
            Text = text,
            Source = "tool.send_message",
        };
        return _delivery.DeliverAsync(message, ct);
    }
}
