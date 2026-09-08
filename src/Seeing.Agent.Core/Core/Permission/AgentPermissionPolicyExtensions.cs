using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// AgentPermissionPolicy 策略合并扩展 - 承载策略组合业务逻辑（交集、快照合并）。
/// <para>策略定义（DTO）位于 Abstractions，组合逻辑留在主库，保持零实现纪律。</para>
/// </summary>
public static class AgentPermissionPolicyExtensions
{
    /// <summary>
    /// 合并 Session 权限快照规则（Deny 写入 DeniedTools + Rules）。
    /// </summary>
    public static AgentPermissionPolicy WithSessionSnapshot(
        this AgentPermissionPolicy policy,
        IReadOnlyList<PermissionRuleEntry> snapshotRules)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (snapshotRules == null || snapshotRules.Count == 0)
            return policy;

        var mergedRules = policy.Rules.Concat(snapshotRules).ToList();
        var denied = new HashSet<string>(policy.DeniedTools, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in snapshotRules)
        {
            if (rule.Effect == PermissionEffect.Deny &&
                rule.Kind == PermissionKind.Tool &&
                !string.IsNullOrEmpty(rule.Pattern) &&
                rule.Pattern != "*")
            {
                denied.Add(rule.Pattern);
            }
        }

        return new AgentPermissionPolicy
        {
            AgentName = policy.AgentName,
            Rules = mergedRules,
            AllowedTools = policy.AllowedTools,
            DeniedTools = denied.ToList(),
            AllowedAgents = policy.AllowedAgents,
            AllowedMcpServers = policy.AllowedMcpServers,
            DefaultEffect = policy.DefaultEffect,
            ContentHash = ComputeHash(mergedRules)
        };
    }

    /// <summary>
    /// 与另一个策略求交集（用于委托）
    /// </summary>
    public static AgentPermissionPolicy Intersect(
        this AgentPermissionPolicy policy,
        AgentPermissionPolicy other)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(other);

        var mergedRules = new List<PermissionRuleEntry>();

        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            var thisRules = policy.Rules.Where(r => r.Kind == kind).ToList();
            var otherRules = other.Rules.Where(r => r.Kind == kind).ToList();
            mergedRules.AddRange(MergeRuleSets(thisRules, otherRules, kind));
        }

        var mergedAllowedTools = policy.AllowedTools.Count > 0 && other.AllowedTools.Count > 0
            ? policy.AllowedTools.Intersect(other.AllowedTools, StringComparer.OrdinalIgnoreCase).ToList()
            : policy.AllowedTools.Count > 0 ? policy.AllowedTools : other.AllowedTools;

        var mergedDeniedTools = policy.DeniedTools.Union(other.DeniedTools, StringComparer.OrdinalIgnoreCase).ToList();
        var mergedDefault = ChooseStrictDefault(policy.DefaultEffect, other.DefaultEffect);

        return new AgentPermissionPolicy
        {
            AgentName = $"{policy.AgentName}∩{other.AgentName}",
            Rules = mergedRules,
            AllowedTools = mergedAllowedTools,
            DeniedTools = mergedDeniedTools,
            AllowedAgents = policy.AllowedAgents.Intersect(other.AllowedAgents).ToList(),
            AllowedMcpServers = policy.AllowedMcpServers.Intersect(other.AllowedMcpServers).ToList(),
            DefaultEffect = mergedDefault,
            ContentHash = ComputeHash(mergedRules)
        };
    }

    private static IEnumerable<PermissionRuleEntry> MergeRuleSets(
        List<PermissionRuleEntry> set1, List<PermissionRuleEntry> set2, PermissionKind kind)
    {
        var allow1 = set1.Where(r => r.Effect == PermissionEffect.Allow).ToList();
        var allow2 = set2.Where(r => r.Effect == PermissionEffect.Allow).ToList();

        if (allow1.Count > 0 && allow2.Count > 0)
        {
            foreach (var r1 in allow1)
            {
                foreach (var r2 in allow2)
                {
                    if (PatternsIntersect(r1.Pattern, r2.Pattern, out var intersection))
                    {
                        yield return new PermissionRuleEntry
                        {
                            Kind = kind,
                            Pattern = intersection,
                            Effect = PermissionEffect.Allow,
                            Priority = Math.Max(r1.Priority, r2.Priority),
                            Source = $"{r1.Source}∩{r2.Source}",
                            Delegable = r1.Delegable && r2.Delegable
                        };
                    }
                }
            }
        }
        else if (allow1.Count > 0) { foreach (var r in allow1) yield return r; }
        else if (allow2.Count > 0) { foreach (var r in allow2) yield return r; }

        foreach (var r in set1.Where(r => r.Effect == PermissionEffect.Deny)) yield return r;
        foreach (var r in set2.Where(r => r.Effect == PermissionEffect.Deny)) yield return r;
    }

    private static bool PatternsIntersect(string pattern1, string pattern2, out string intersection)
    {
        intersection = string.Empty;
        if (pattern1 == pattern2) { intersection = pattern1; return true; }
        if (pattern1 == "*") { intersection = pattern2; return true; }
        if (pattern2 == "*") { intersection = pattern1; return true; }

        if (pattern1.EndsWith("/*") && pattern2.EndsWith("/*"))
        {
            var prefix1 = pattern1[..^2];
            var prefix2 = pattern2[..^2];
            if (prefix2.StartsWith(prefix1)) { intersection = pattern2; return true; }
            if (prefix1.StartsWith(prefix2)) { intersection = pattern1; return true; }
        }

        return false;
    }

    /// <summary>
    /// 选择更严格的默认效果 - 安全优先原则
    /// 严格程度：Deny > Ask > Allow
    /// </summary>
    private static PermissionEffect ChooseStrictDefault(PermissionEffect a, PermissionEffect b)
    {
        // Deny 始终最严格，Ask 次之，Allow 最宽松
        var strictness = new Dictionary<PermissionEffect, int>
        {
            [PermissionEffect.Deny] = 2,
            [PermissionEffect.Ask] = 1,
            [PermissionEffect.Allow] = 0
        };
        return strictness[a] >= strictness[b] ? a : b;
    }

    private static string ComputeHash(IReadOnlyList<PermissionRuleEntry> rules)
    {
        var json = JsonSerializer.Serialize(rules);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }
}
