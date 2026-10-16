using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using System.Runtime.CompilerServices;

namespace Seeing.Gateway.Client;

/// <summary>
/// 远程会话面：包装同一 <see cref="WebSocketGatewayClient"/> 实例（与通道宿主面单连接）。
/// </summary>
public sealed class RemoteSessionClient : ISessionClient
{
    private readonly WebSocketGatewayClient _connection;
    private readonly SemaphoreSlim _subscribeLock = new(1, 1);

    public RemoteSessionClient(WebSocketGatewayClient connection)
    {
        _connection = connection;
    }

    public async Task<GatewaySubmitAckPayload> SubmitAsync(
        GatewayRequest request,
        CancellationToken ct = default)
    {
        var result = await _connection.SubmitAsync(request, ct).ConfigureAwait(false);
        return new GatewaySubmitAckPayload
        {
            SessionId = string.IsNullOrEmpty(result.SessionId) ? request.SessionId : result.SessionId,
            Success = result.Success,
            Error = result.Error,
            PendingInputCount = result.PendingInputCount
        };
    }

    public async IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = sessionId;
        await _subscribeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var inbound in _connection.ReceiveAsync(ct).ConfigureAwait(false))
            {
                switch (inbound.Type)
                {
                    case GatewayWsFrameType.ChatEvent when inbound.Event != null:
                        yield return inbound.Event;
                        break;

                    case GatewayWsFrameType.ExecutionComplete:
                        yield break;

                    case GatewayWsFrameType.ChatError:
                        throw new InvalidOperationException(inbound.Error?.Message ?? "Subscribe failed");
                }
            }
        }
        finally
        {
            _subscribeLock.Release();
        }
    }

    public async Task<bool> CancelAsync(string sessionId, CancellationToken ct = default)
    {
        var ack = await _connection.CancelAsync(sessionId, ct).ConfigureAwait(false);
        return ack.Cancelled;
    }

    public Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken ct = default) =>
        _connection.RespondPermissionAsync(sessionId, permissionId, allow, reason, ct);
}
