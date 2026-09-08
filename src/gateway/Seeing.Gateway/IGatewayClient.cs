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

    IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        string executionId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        string executionId,
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
