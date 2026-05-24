using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限规则条目 - 新权限系统的规则定义
/// </summary>
public sealed class PermissionRuleEntry
{
    /// <summary>规则唯一标识</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>权限类型</summary>
    public PermissionKind Kind { get; init; }
    
    /// <summary>匹配模式（支持通配符 * 和 ?）</summary>
    public string Pattern { get; init; } = "*";
    
    /// <summary>命名空间（可选）</summary>
    public string? Namespace { get; init; }
    
    /// <summary>权限效果</summary>
    public PermissionEffect Effect { get; init; } = PermissionEffect.Deny;
    
    /// <summary>条件集合</summary>
    public PermissionConditionSet? Conditions { get; init; }
    
    /// <summary>优先级（数值越大优先级越高）</summary>
    public int Priority { get; init; }
    
    /// <summary>规则来源</summary>
    public string Source { get; init; } = "builtin";
    
    /// <summary>是否可委托给子代理</summary>
    public bool Delegable { get; init; } = true;
    
    /// <summary>生存时间</summary>
    public TimeSpan? TimeToLive { get; init; }
    
    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>描述</summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// 检查是否匹配指定资源
    /// </summary>
    /// <param name="resource">资源标识符</param>
    /// <returns>是否匹配</returns>
    public bool Matches(ResourceIdentifier resource)
    {
        if (resource.Kind != Kind) return false;
        if (!string.IsNullOrEmpty(Namespace) && resource.Namespace != Namespace) return false;
        return WildcardMatch(Pattern, resource.Name);
    }
    
    /// <summary>
    /// 通配符匹配算法
    /// </summary>
    /// <param name="pattern">模式（支持 * 和 ?）</param>
    /// <param name="input">输入字符串</param>
    /// <returns>是否匹配</returns>
    public static bool WildcardMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return string.IsNullOrEmpty(input);
        if (pattern == "*") return true;
        if (string.IsNullOrEmpty(input)) return false;
        
        int patternIndex = 0, inputIndex = 0, starIndex = -1, matchIndex = 0;
        
        while (inputIndex < input.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == input[inputIndex] || pattern[patternIndex] == '?'))
            {
                patternIndex++; inputIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                matchIndex = inputIndex;
                patternIndex++;
            }
            else if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                inputIndex = matchIndex;
            }
            else return false;
        }
        
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
    
    /// <summary>
    /// 创建允许规则
    /// </summary>
    public static PermissionRuleEntry Allow(PermissionKind kind, string pattern, int priority = 0, string? ns = null)
        => new() { Kind = kind, Pattern = pattern, Namespace = ns, Effect = PermissionEffect.Allow, Priority = priority };
    
    /// <summary>
    /// 创建拒绝规则
    /// </summary>
    public static PermissionRuleEntry Deny(PermissionKind kind, string pattern, int priority = 100, string? ns = null)
        => new() { Kind = kind, Pattern = pattern, Namespace = ns, Effect = PermissionEffect.Deny, Priority = priority };
    
    /// <inheritdoc/>
    public override string ToString() => $"[{Id}] {Kind}:{Pattern} -> {Effect} (P{Priority})";
}

/// <summary>
/// 权限条件 - 单个条件定义
/// </summary>
public sealed record PermissionCondition
{
    /// <summary>条件键</summary>
    public string Key { get; init; } = string.Empty;
    
    /// <summary>条件值</summary>
    public object? Value { get; init; }
    
    /// <summary>条件运算符</summary>
    public ConditionOperator Operator { get; init; } = ConditionOperator.Equals;
    
    /// <summary>条件描述</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 权限条件集合 - 条件组合
/// </summary>
public sealed record PermissionConditionSet
{
    /// <summary>条件列表</summary>
    public IReadOnlyList<PermissionCondition> Conditions { get; init; } = Array.Empty<PermissionCondition>();
    
    /// <summary>组合逻辑</summary>
    public ConditionLogic Logic { get; init; } = ConditionLogic.And;
    
    /// <summary>
    /// 创建 AND 条件集合
    /// </summary>
    public static PermissionConditionSet And(params PermissionCondition[] conditions)
        => new() { Conditions = conditions.ToList(), Logic = ConditionLogic.And };
    
    /// <summary>
    /// 创建 OR 条件集合
    /// </summary>
    public static PermissionConditionSet Or(params PermissionCondition[] conditions)
        => new() { Conditions = conditions.ToList(), Logic = ConditionLogic.Or };
}
