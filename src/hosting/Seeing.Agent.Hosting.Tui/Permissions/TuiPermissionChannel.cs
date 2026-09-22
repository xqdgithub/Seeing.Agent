using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Hosting.Tui.Permissions;

/// <summary>
/// TUI 事件流权限通道：请求呈现与收敛由权限管理器经执行事件流广播，
/// TUI 经 <c>TuiPermissionQueue</c> 取在途请求并回传裁决，通道本身不持有挂起状态。
/// <para>与 <c>Hosting.Web</c> 的 <c>EventStreamPermissionChannel</c> 语义完全一致。</para>
/// </summary>
public sealed class TuiPermissionChannel : IPermissionChannel
{
    /// <inheritdoc />
    public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;

    /// <inheritdoc />
    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default) => ValueTask.CompletedTask;
}
