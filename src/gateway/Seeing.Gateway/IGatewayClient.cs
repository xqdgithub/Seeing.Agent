using Seeing.Gateway.Models;

namespace Seeing.Gateway;

/// <summary>
/// 网关客户端契约（Submit / Subscribe / Cancel）
/// </summary>
public interface IGatewayClient
{
    Task<GatewaySubmitResult> SubmitAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>会话级订阅：流式转发事件直到会话消费循环空闲或超时，不按 executionId 过滤。</summary>
    IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<GatewaySessionResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
