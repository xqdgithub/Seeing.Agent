using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限评估结果 - 包含完整评估路径和审计信息
/// </summary>
public sealed class PermissionResult
{
    /// <summary>权限效果</summary>
    public PermissionEffect Effect { get; init; }

    /// <summary>资源标识符</summary>
    public ResourceIdentifier Resource { get; init; } = default;

    /// <summary>决策原因</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>匹配的规则</summary>
    public PermissionRuleEntry? MatchedRule { get; init; }

    /// <summary>评估路径</summary>
    public IReadOnlyList<PermissionEvaluationStep> EvaluationPath { get; init; } = Array.Empty<PermissionEvaluationStep>();

    /// <summary>上下文哈希</summary>
    public string ContextHash { get; init; } = string.Empty;

    /// <summary>评估时间</summary>
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>缓存 TTL</summary>
    public TimeSpan? CacheTtl { get; init; }

    /// <summary>是否来自缓存</summary>
    public bool FromCache { get; init; }

    /// <summary>是否允许</summary>
    public bool IsAllowed => Effect == PermissionEffect.Allow;

    /// <summary>是否拒绝</summary>
    public bool IsDenied => Effect == PermissionEffect.Deny;

    /// <summary>是否需要确认</summary>
    public bool NeedsConfirmation => Effect == PermissionEffect.Ask;

    /// <summary>
    /// 创建允许结果
    /// </summary>
    public static PermissionResult Allow(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Allow, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };

    /// <summary>
    /// 创建拒绝结果
    /// </summary>
    public static PermissionResult Deny(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Deny, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };

    /// <summary>
    /// 创建需要确认的结果
    /// </summary>
    public static PermissionResult Ask(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Ask, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };

    /// <summary>
    /// 转换为 PermissionDecision
    /// </summary>
    /// <returns>兼容旧接口的决策结果</returns>
    public PermissionDecision ToDecision()
    {
        var action = Effect switch
        {
            PermissionEffect.Allow => PermissionAction.Allow,
            PermissionEffect.Deny => PermissionAction.Deny,
            PermissionEffect.Ask => PermissionAction.Ask,
            _ => PermissionAction.Deny
        };
        return new PermissionDecision(action, Reason, null);
    }

    /// <inheritdoc/>
    public override string ToString() => $"[{Effect}] {Resource}: {Reason}";
}

/// <summary>
/// 权限评估步骤 - 记录评估过程
/// </summary>
public sealed class PermissionEvaluationStep
{
    /// <summary>步骤名称</summary>
    public string Step { get; init; } = string.Empty;

    /// <summary>输入</summary>
    public object? Input { get; init; }

    /// <summary>输出</summary>
    public object? Output { get; init; }

    /// <summary>是否匹配</summary>
    public bool Matched { get; init; }

    /// <summary>耗时</summary>
    public TimeSpan Duration { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Step}: {(Matched ? "OK" : "X")} {Input} -> {Output}";
}
