using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 权限动作
    /// </summary>
    public enum PermissionAction
    {
        /// <summary>允许</summary>
        Allow,

        /// <summary>拒绝</summary>
        Deny,

        /// <summary>询问用户</summary>
        Ask
    }

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

    /// <summary>
    /// 权限决策结果
    /// </summary>
    public record PermissionDecision
    {
        /// <summary>决策动作</summary>
        public PermissionAction Action { get; init; }

        /// <summary>决策原因</summary>
        public string? Reason { get; init; }

        /// <summary>匹配的规则</summary>
        public PermissionRuleEntry? MatchedRule { get; init; }

        public PermissionDecision(PermissionAction action, string? reason = null, PermissionRuleEntry? matchedRule = null)
        {
            Action = action;
            Reason = reason;
            MatchedRule = matchedRule;
        }

        /// <summary>允许</summary>
        public static PermissionDecision Allow(string? reason = null)
            => new(PermissionAction.Allow, reason);

        /// <summary>拒绝</summary>
        public static PermissionDecision Deny(string? reason = null, PermissionRuleEntry? rule = null)
            => new(PermissionAction.Deny, reason, rule);

        /// <summary>需要确认</summary>
        public static PermissionDecision Ask(string? reason = null, PermissionRuleEntry? rule = null)
            => new(PermissionAction.Ask, reason, rule);
    }

    /// <summary>
    /// 条件运算符
    /// </summary>
    public enum ConditionOperator
    {
        Equals = 0,
        NotEquals = 1,
        Contains = 2,
        StartsWith = 3,
        EndsWith = 4,
        Matches = 5,
        NotContains = 6,
        GreaterThan = 7,
        LessThan = 8,
        InRange = 9,
        FileExists = 10,
        DirectoryExists = 11,
        IsSubPathOf = 12
    }
}
