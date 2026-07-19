using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway.Client;

/// <summary>
/// 从 WebSocket 流解析 JSON Text Frame
/// </summary>
public static class GatewayWsFrameReader
{
    public static async IAsyncEnumerable<GatewayWsFrame> ReadFramesAsync(
        ClientWebSocket webSocket,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    yield break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.ToArray());
            var frame = GatewayWsFrameSerializer.Deserialize(json);
            if (frame != null)
                yield return frame;
        }
    }
}

/// <summary>
/// 将 WS 帧解析为 <see cref="GatewayInbound"/>
/// </summary>
public static class GatewayInboundParser
{
    public static GatewayInbound Parse(GatewayWsFrame frame)
    {
        var inbound = new GatewayInbound
        {
            Type = frame.Type,
            Id = frame.Id
        };

        if (frame.Payload == null)
            return inbound;

        switch (frame.Type)
        {
            case GatewayWsFrameType.Connected:
                return inbound with
                {
                    Connected = frame.Payload.Value.Deserialize<GatewayConnectedPayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.ChatEvent:
                return inbound with
                {
                    Event = frame.Payload.Value.Deserialize<GatewayEvent>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.SubmitAck:
                return inbound with
                {
                    SubmitAck = frame.Payload.Value.Deserialize<GatewaySubmitAckPayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.ExecutionComplete:
                return inbound with
                {
                    ExecutionComplete = frame.Payload.Value.Deserialize<GatewayExecutionCompletePayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.ChatError:
            case GatewayWsFrameType.Error:
                return inbound with
                {
                    Error = frame.Payload.Value.Deserialize<GatewayWsErrorPayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.CancelAck:
                return inbound with
                {
                    CancelAck = frame.Payload.Value.Deserialize<GatewayCancelAckPayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.PermissionAck:
                return inbound with
                {
                    PermissionAck = frame.Payload.Value.Deserialize<GatewayPermissionRespondResult>(GatewayWsFrameSerializer.JsonOptions)
                };

            case GatewayWsFrameType.ChannelOutbound:
                return inbound with
                {
                    ChannelOutbound = frame.Payload.Value.Deserialize<GatewayChannelOutboundPayload>(GatewayWsFrameSerializer.JsonOptions)
                };

            default:
                return inbound;
        }
    }
}
