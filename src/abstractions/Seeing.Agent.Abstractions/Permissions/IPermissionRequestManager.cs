using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 权限在途请求管理器：在通用交互基础设施之上附加权限领域决议重载。
/// </summary>
public interface IPermissionRequestManager : IPendingRequestManager<PermissionRequest, PermissionResolution>
{
    /// <summary>幂等完成（UI/Gateway/重评估/取消共用）；expectedSessionId 非空时校验归属。</summary>
    bool TryResolve(string requestId, PermissionEffect decision, PermissionGrantScope scope,
                    PermissionResolvedBy resolvedBy, string? reason = null, string? expectedSessionId = null);
}
