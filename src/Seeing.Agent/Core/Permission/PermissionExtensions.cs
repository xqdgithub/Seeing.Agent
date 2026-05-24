using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限类型扩展方法 - 新旧系统类型转换
/// </summary>
public static class PermissionExtensions
{
    /// <summary>
    /// 将 PermissionEffect 转换为 PermissionAction
    /// </summary>
    public static PermissionAction ToAction(this PermissionEffect effect)
    {
        return effect switch
        {
            PermissionEffect.Allow => PermissionAction.Allow,
            PermissionEffect.Deny => PermissionAction.Deny,
            PermissionEffect.Ask => PermissionAction.Ask,
            _ => PermissionAction.Deny
        };
    }

    /// <summary>
    /// 将 PermissionAction 转换为 PermissionEffect
    /// </summary>
    public static PermissionEffect ToEffect(this PermissionAction action)
    {
        return action switch
        {
            PermissionAction.Allow => PermissionEffect.Allow,
            PermissionAction.Deny => PermissionEffect.Deny,
            PermissionAction.Ask => PermissionEffect.Ask,
            _ => PermissionEffect.Deny
        };
    }

    /// <summary>
    /// 将 PermissionDecision 转换为 PermissionResult
    /// </summary>
    public static PermissionResult ToResult(this PermissionDecision decision, ResourceIdentifier resource)
    {
        return new PermissionResult
        {
            Effect = decision.Action.ToEffect(),
            Resource = resource,
            Reason = decision.Reason ?? string.Empty
        };
    }
}
