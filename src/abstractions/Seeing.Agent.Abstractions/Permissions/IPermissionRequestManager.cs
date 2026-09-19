namespace Seeing.Agent.Abstractions.Permissions;

public interface IPermissionRequestManager : IDisposable
{
    /// <summary>登记在途并发布请求事件（分配 RequestId）；在途 ≥ 32 → 立即 Deny(NoChannel,"队列已满")。</summary>
    Task<PermissionTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default);

    Task<PermissionResolution> WaitAsync(PermissionTicket ticket, CancellationToken ct = default);

    /// <summary>幂等完成（UI/Gateway/重评估/取消共用）；expectedSessionId 非空时校验归属。</summary>
    bool TryResolve(string requestId, PermissionEffect decision, PermissionGrantScope scope,
                    PermissionResolvedBy resolvedBy, string? reason = null, string? expectedSessionId = null);

    IReadOnlyList<PermissionRequest> GetPending(string sessionId);

    /// <summary>全部未完成在途请求（不做会话过滤；最终一致性依据）。</summary>
    IReadOnlyList<PermissionRequest> GetAllPending();

    int PendingCount { get; }

    /// <summary>在途集合变化（尽力通知；订阅者异常须隔离）。</summary>
    event Action? PendingChanged;
}
