using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Hosting.Web.Permissions;

/// <summary>
/// Web 事件流权限通道：请求呈现与收敛由权限管理器经执行事件流广播，
/// UI 决策经 <c>PermissionInteractionService</c> 回传，通道本身不持有挂起状态。
/// </summary>
public sealed class EventStreamPermissionChannel : IPermissionChannel
{
    /// <inheritdoc />
    public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;

    /// <inheritdoc />
    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default) => ValueTask.CompletedTask;
}
