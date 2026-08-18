using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 权限评估器接口 - 纯粹的权限评估职责
    /// </summary>
    public interface IRuleEvaluator
    {
        /// <summary>评估单个权限请求</summary>
        PermissionDecision Evaluate(string permission, string pattern);

        /// <summary>评估工具调用权限</summary>
        PermissionDecision EvaluateTool(string toolId, IExecutionContext? ctx = null);

        /// <summary>评估 Agent 行动权限</summary>
        PermissionDecision EvaluateAction(string action, IDictionary<string, object>? context = null);

        /// <summary>检查工具是否被禁用</summary>
        bool IsToolDisabled(string toolId);
    }
}