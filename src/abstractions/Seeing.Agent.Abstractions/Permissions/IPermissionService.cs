namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>宿主/规则 API（精简后）；新增资源门编排入口。</summary>
public interface IPermissionService
{
    Task<PermissionResult> EvaluateToolAsync(string toolName, string? ns, PermissionContext context, CancellationToken ct = default);

    Task<PermissionResult> EvaluateSkillAsync(string skillName, PermissionContext context, CancellationToken ct = default);

    Task<AgentPermissionPolicy> GetPolicyAsync(string agentName, CancellationToken ct = default);

    AgentPermissionPolicy MergePolicies(AgentPermissionPolicy global, AgentPermissionPolicy agent);

    void InvalidateCache(string? agentName = null, string? resourcePattern = null);

    Task LogAuditAsync(PermissionResult result, PermissionContext context, CancellationToken ct = default);

    Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default);
}
