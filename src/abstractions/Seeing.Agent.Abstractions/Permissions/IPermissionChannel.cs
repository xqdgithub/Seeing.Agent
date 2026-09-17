namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>宿主审批通道：呈现与宿主级自动批准策略。实现必须幂等。</summary>
public interface IPermissionChannel
{
    /// <summary>宿主级自动批准策略（如 Gateway auto_approve）；null=不短路。</summary>
    PermissionEffect? TryAutoApprove(PermissionRequest request);

    ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default);

    ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default);
}
