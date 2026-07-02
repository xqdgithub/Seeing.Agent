using System.Threading.Channels;
using Seeing.Gateway.Models;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 事件 sink：供 PermissionChannel 等向 Chat 流注入 side-channel 事件
/// </summary>
public interface IGatewayEventSink
{
    void Emit(GatewayEvent gatewayEvent);
}

/// <summary>
/// 基于 Channel 的事件 sink
/// </summary>
public sealed class ChannelGatewayEventSink(ChannelWriter<GatewayEvent> writer) : IGatewayEventSink
{
    public void Emit(GatewayEvent gatewayEvent)
    {
        if (!writer.TryWrite(gatewayEvent))
            _ = writer.WriteAsync(gatewayEvent);
    }
}

/// <summary>
/// 合并 Channel sink 与 WS 连接推送
/// </summary>
public sealed class CompositeGatewayEventSink(
    IGatewayEventSink primary,
    GatewayConnectionManager? connectionManager = null) : IGatewayEventSink
{
    public void Emit(GatewayEvent gatewayEvent)
    {
        primary.Emit(gatewayEvent);
        connectionManager?.PushEvent(gatewayEvent.SessionId, gatewayEvent);
    }
}

/// <summary>
/// 单次 Chat 运行的权限上下文（AsyncLocal）
/// </summary>
public sealed class PermissionRunContext
{
    public required string SessionId { get; init; }

    public string? LoopId { get; init; }

    public required IGatewayEventSink Sink { get; init; }
}
