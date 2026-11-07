using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Hosting;

/// <summary>
/// 事件流承载的权限通道：请求的呈现与收敛由权限管理器经执行事件流广播，
/// UI/TUI 各自从在途请求投影取卡片并回传裁决——通道本身不持有挂起状态。
/// <para>
/// <b>为什么只有这一个实现：</b>此前 Web 与 Tui 各有一个逐字相同、只改类型名的副本
/// （<c>EventStreamPermissionChannel</c> / <c>TuiPermissionChannel</c>），连注释都写着「语义完全一致」。
/// 两者行为都是「三个方法全 no-op」，差异只来自宿主如何消费事件流，因此合并为一个共享类型，
/// 由各 Shape 注册时选择（交互式形态用它，无交互形态用 <c>DenyAllPermissionChannel</c>）。
/// </para>
/// </summary>
public sealed class EventStreamPermissionChannel : IPermissionChannel
{
    /// <inheritdoc />
    /// <remarks>恒 null：自动批准由 <c>EffectivePermissionPolicy</c>/授权器链决定，通道不参与。</remarks>
    public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;

    /// <inheritdoc />
    /// <remarks>空实现：呈现由事件流消费者（UI 卡片）完成。</remarks>
    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    /// <remarks>空实现：收敛同样经事件流广播。</remarks>
    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
