using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 默认权限策略提供者 - 提供基础的权限策略
/// </summary>
public sealed class DefaultPermissionPolicyProvider : IPermissionPolicyProvider
{
    /// <inheritdoc />
    public PermissionPolicy GetPolicy(string agentName)
    {
        return new PermissionPolicy
        {
            AgentName = agentName,
            AllowedTools = null,  // 不限制
            AllowedSkills = null
        };
    }

    /// <inheritdoc />
    public PermissionPolicy Merge(PermissionPolicy global, PermissionPolicy agent)
    {
        return new PermissionPolicy
        {
            AgentName = agent.AgentName,
            AllowedTools = global.AllowedTools ?? agent.AllowedTools,
            AllowedSkills = global.AllowedSkills ?? agent.AllowedSkills
        };
    }
}
