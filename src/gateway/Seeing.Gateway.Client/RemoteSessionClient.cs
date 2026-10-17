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
            RequestId = result.RequestId,
            Success = result.Success,
            Error = result.Error,
            PendingInputCount = result.PendingInputCount
        };
    }

    /// <summary>无关联过滤的订阅（历史行为：终止于任意 execution.complete）。仅 UI/展示用途；请求/应答应使用带 requestId 的重载。</summary>
    public IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(sessionId, requestId: null, ct);

    /// <summary>
    /// 请求/应答订阅：按 submit 关联 ID 过滤共享入站队列。
    /// <para>
    /// 缺陷背景（QQ「(空回复)」）：桥在 RunCompleted 提前 return 弃置订阅枚举后，
    /// 滞留队列的陈旧 execution.complete 帧会立刻终止下一轮订阅 → (空回复)。
    /// 按 requestId 关联后：他轮的 ChatEvent / ExecutionComplete 帧一律跳过，
    /// 仅匹配轮次的 ExecutionComplete 收口订阅。
    /// </para>
    /// </summary>
    public IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        string? requestId,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(sessionId, requestId, ct);

    private async IAsyncEnumerable<GatewayEvent> SubscribeCoreAsync(
        string sessionId,
        string? requestId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = sessionId;
        await _subscribeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var inbound in _connection.ReceiveAsync(ct).ConfigureAwait(false))
            {
                // 关联过滤（严格模式）：仅接受与本轮 submit 关联的帧。
                // 他轮帧（含桥提前 return 滞留的陈旧 execution.complete）与无关联帧
                // （如 GatewayDeliveryNotifier 投递通知）一律跳过。
                if (requestId != null && !string.Equals(inbound.Id, requestId, StringComparison.Ordinal))
                    continue;

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
