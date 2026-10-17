using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway.Client;

/// <summary>
/// 会话面客户端：Submit / Subscribe / Cancel / 权限响应。
/// 进程内（<see cref="InProcessSessionClient"/>）与远程（<see cref="RemoteSessionClient"/>）共用此契约。
/// </summary>
public interface ISessionClient
{
    Task<GatewaySubmitAckPayload> SubmitAsync(GatewayRequest request, CancellationToken ct = default);

    IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 请求/应答订阅：按 submit 关联 ID 过滤入站帧（remote/WS 实现按 requestId 关联；
    /// 进程内实现忽略 requestId）。请求/应答收集（如渠道桥回复收集）应使用本重载，
    /// 避免共享入站队列中他轮滞留帧（陈旧 execution.complete）造成的串轮/提前收口。
    /// </summary>
    IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, string? requestId, CancellationToken ct = default);

    /// <summary>默认 interrupt。</summary>
    Task<bool> CancelAsync(string sessionId, CancellationToken ct = default);

    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId, string permissionId, bool allow, string? reason = null, CancellationToken ct = default);
}
