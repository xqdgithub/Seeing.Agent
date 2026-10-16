using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 出站成功后把调度注入消息推给已订阅该会话的 WebSocket 连接。
/// </summary>
public sealed class GatewayDeliveryNotifier : IChannelDeliveryNotifier
{
    private readonly GatewayConnectionManager _connectionManager;

    public GatewayDeliveryNotifier(GatewayConnectionManager connectionManager)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    }

    public Task NotifyDeliveredAsync(
        OutboundMessage message,
        OutboundDeliveryResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        if (!result.Delivered || string.IsNullOrEmpty(message.CorrelationSessionId))
            return Task.CompletedTask;

        _connectionManager.PushEvent(message.CorrelationSessionId, new GatewayEvent
        {
            Object = GatewayEventObject.Message,
            Status = GatewayEventStatus.Completed,
            SessionId = message.CorrelationSessionId,
            SourceType = message.Source,
            Data = new GatewayEventData
            {
                Role = "assistant",
                Text = message.Text
            }
        });

        return Task.CompletedTask;
    }
}
