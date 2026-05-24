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
        public PermissionRule? MatchedRule { get; init; }

        public PermissionDecision(PermissionAction action, string? reason = null, PermissionRule? matchedRule = null)
        {
            Action = action;
            Reason = reason;
            MatchedRule = matchedRule;
        }

        /// <summary>允许</summary>
        public static PermissionDecision Allow(string? reason = null) 
            => new(PermissionAction.Allow, reason);
        
        /// <summary>拒绝</summary>
        public static PermissionDecision Deny(string? reason = null, PermissionRule? rule = null) 
            => new(PermissionAction.Deny, reason, rule);
        
        /// <summary>需要确认</summary>
        public static PermissionDecision Ask(string? reason = null, PermissionRule? rule = null) 
            => new(PermissionAction.Ask, reason, rule);
    }

    /// <summary>
    /// 权限策略提供者接口 - Agent 的权限声明
    /// </summary>
    public interface IPermissionPolicyProvider
    {
        /// <summary>获取 Agent 的权限策略</summary>
        PermissionPolicy GetPolicy(string agentName);
        
        /// <summary>合并全局策略与 Agent 策略</summary>
        PermissionPolicy Merge(PermissionPolicy global, PermissionPolicy agent);
    }

    /// <summary>
    /// 权限策略 - Agent 声明的权限需求
    /// </summary>
    public class PermissionPolicy
    {
        /// <summary>Agent 名称</summary>
        public string AgentName { get; init; } = string.Empty;
        
        /// <summary>允许的权限</summary>
        public IReadOnlyList<PermissionGrant> Grants { get; init; } = Array.Empty<PermissionGrant>();
        
        /// <summary>拒绝的权限</summary>
        public IReadOnlyList<PermissionDeny> Denies { get; init; } = Array.Empty<PermissionDeny>();
        
        /// <summary>允许的工具列表</summary>
        public IReadOnlyList<string>? AllowedTools { get; init; }
        
        /// <summary>允许的技能列表</summary>
        public IReadOnlyList<string>? AllowedSkills { get; init; }
    }

    /// <summary>
    /// 权限授予
    /// </summary>
    public record PermissionGrant(
        string Permission, 
        string Pattern, 
        PermissionCondition? Condition = null);

    /// <summary>
    /// 权限拒绝
    /// </summary>
    public record PermissionDeny(
        string Permission, 
        string Pattern);

    /// <summary>
    /// 权限条件
    /// </summary>
    public record PermissionCondition(
        string Key, 
        object Value, 
        ConditionOperator Operator = ConditionOperator.Equals);

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
        // 新增值 - 用于权限系统条件评估
        NotContains = 6,
        GreaterThan = 7,
        LessThan = 8,
        InRange = 9,
        FileExists = 10,
        DirectoryExists = 11,
        IsSubPathOf = 12
    }
}