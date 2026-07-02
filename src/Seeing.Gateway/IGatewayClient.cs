using Seeing.Gateway.Models;

namespace Seeing.Gateway;

/// <summary>
/// 网关客户端契约
/// </summary>
public interface IGatewayClient
{
    IAsyncEnumerable<GatewayEvent> ChatAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default);

    Task StopChatAsync(
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
}
