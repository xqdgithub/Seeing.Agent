using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 默认权限通道 — 安全默认：拒绝所有操作
/// </summary>
public class DefaultPermissionChannel : IPermissionChannel
{
    public bool AutoApproveAll { get; init; } = false;

    public static readonly DefaultPermissionChannel Instance = new();
    public static readonly DefaultPermissionChannel AutoApproveInstance = new() { AutoApproveAll = true };

    public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        if (AutoApproveAll)
            return Task.FromResult(PermissionChannelResult.Allowed());

        throw new PermissionRequiredException(
            request.Resource ?? "未知资源",
            "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true 以自动批准所有操作（危险），或提供 IPermissionChannel 实现。");
    }
}

/// <summary>
/// 立即拒绝所有权限请求的通道
/// </summary>
public sealed class DenyAllPermissionChannel : IPermissionChannel
{
    public static readonly DenyAllPermissionChannel Instance = new();

    public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        => Task.FromResult(PermissionChannelResult.Denied("后台执行模式：未配置权限通道，请求被拒绝"));
}
