using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>默认投递通知：不向宿主呈现端转发。</summary>
public sealed class NoopDeliveryNotifier : IChannelDeliveryNotifier
{
    public Task NotifyDeliveredAsync(
        OutboundMessage message, OutboundDeliveryResult result, CancellationToken ct = default)
        => Task.CompletedTask;
}
