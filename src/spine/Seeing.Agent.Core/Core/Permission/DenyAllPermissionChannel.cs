using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 无交互宿主的默认通道：<c>TryAutoApprove</c> 恒 <c>null</c>，呈现与关闭均为 no-op。
/// </summary>
public sealed class DenyAllPermissionChannel : IPermissionChannel
{
    /// <inheritdoc />
    public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;

    /// <inheritdoc />
    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
