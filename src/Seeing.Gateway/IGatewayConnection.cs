using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway;

/// <summary>
/// Gateway WebSocket 全双工连接契约
/// </summary>
public interface IGatewayConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<GatewaySubmitResult> SubmitAsync(GatewayRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<GatewayInbound> ReceiveAsync(CancellationToken cancellationToken = default);

    Task<GatewayCancelAckPayload> CancelAsync(string executionId, CancellationToken cancellationToken = default);

    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// WebSocket 入站消息（统一消费契约）
/// </summary>
public record GatewayInbound
{
    public required GatewayWsFrameType Type { get; init; }

    public string? Id { get; init; }

    public GatewayEvent? Event { get; init; }

    public GatewaySubmitAckPayload? SubmitAck { get; init; }

    public GatewayExecutionCompletePayload? ExecutionComplete { get; init; }

    public GatewayWsErrorPayload? Error { get; init; }

    public GatewayCancelAckPayload? CancelAck { get; init; }

    public GatewayPermissionRespondResult? PermissionAck { get; init; }

    public GatewayConnectedPayload? Connected { get; init; }

    public GatewayChannelOutboundPayload? ChannelOutbound { get; init; }
}

/// <summary>
/// Gateway Client 传输方式
/// </summary>
public enum GatewayClientTransport
{
    HttpSse,
    WebSocket
}
