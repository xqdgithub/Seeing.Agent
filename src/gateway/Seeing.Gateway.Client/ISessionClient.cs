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

    /// <summary>默认 interrupt。</summary>
    Task<bool> CancelAsync(string sessionId, CancellationToken ct = default);

    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId, string permissionId, bool allow, string? reason = null, CancellationToken ct = default);
}
