using System.Security.Cryptography;
using System.Text.Json;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// Agent 权限策略 - 完整的策略定义
/// </summary>
public sealed class AgentPermissionPolicy
{
    /// <summary>策略唯一标识</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Agent 名称</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>策略版本</summary>
    public int Version { get; init; } = 1;

    /// <summary>权限规则列表</summary>
    public IReadOnlyList<PermissionRuleEntry> Rules { get; init; } = Array.Empty<PermissionRuleEntry>();

    /// <summary>允许的工具列表</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>禁止的工具列表</summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();

    /// <summary>允许的子代理列表</summary>
    public IReadOnlyList<string> AllowedAgents { get; init; } = Array.Empty<string>();

    /// <summary>允许的 MCP 服务器列表</summary>
    public IReadOnlyList<string> AllowedMcpServers { get; init; } = Array.Empty<string>();

    /// <summary>默认效果（当没有匹配规则时）</summary>
    public PermissionEffect DefaultEffect { get; init; } = PermissionEffect.Ask;

    /// <summary>策略签名</summary>
    public string? Signature { get; private set; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>内容哈希</summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>空策略（拒绝所有）</summary>
    public static readonly AgentPermissionPolicy Empty = new()
    {
        DefaultEffect = PermissionEffect.Deny,
        ContentHash = ComputeHash(Array.Empty<PermissionRuleEntry>())
    };

    /// <summary>宽松策略（允许所有）</summary>
    public static readonly AgentPermissionPolicy Permissive = new()
    {
        DefaultEffect = PermissionEffect.Allow,
        Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) },
        ContentHash = ComputeHash(new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) })
    };

    private static string ComputeHash(IReadOnlyList<PermissionRuleEntry> rules)
    {
        using var sha256 = SHA256.Create();
        var json = JsonSerializer.SerializeToUtf8Bytes(rules);
        var hash = sha256.ComputeHash(json);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 签名策略
    /// </summary>
    /// <param name="hmacKey">HMAC 密钥</param>
    public void Sign(byte[] hmacKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var signature = hmac.ComputeHash(payload);
        Signature = Convert.ToBase64String(signature);
        ContentHash = ComputeHash(Rules);
    }

    /// <summary>
    /// 验证策略签名
    /// </summary>
    /// <param name="hmacKey">HMAC 密钥</param>
    /// <returns>签名是否有效</returns>
    public bool VerifySignature(byte[] hmacKey)
    {
        if (string.IsNullOrEmpty(Signature)) return false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var expected = Convert.ToBase64String(hmac.ComputeHash(payload));
        return Signature == expected;
    }

    /// <summary>
    /// 检查是否可以委托给指定 Agent
    /// </summary>
    /// <param name="agentName">目标 Agent 名称</param>
    /// <returns>是否可委托</returns>
    public bool IsDelegableTo(string agentName)
    {
        if (AllowedAgents.Count == 0) return true;
        return AllowedAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 与另一个策略求交集（用于委托）
    /// </summary>
    /// <param name="other">另一个策略</param>
    /// <returns>交集策略</returns>
    public AgentPermissionPolicy Intersect(AgentPermissionPolicy other)
    {
        var mergedRules = new List<PermissionRuleEntry>();

        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            var thisRules = Rules.Where(r => r.Kind == kind).ToList();
            var otherRules = other.Rules.Where(r => r.Kind == kind).ToList();
            mergedRules.AddRange(MergeRuleSets(thisRules, otherRules, kind));
        }

        var mergedAllowedTools = AllowedTools.Count > 0 && other.AllowedTools.Count > 0
            ? AllowedTools.Intersect(other.AllowedTools, StringComparer.OrdinalIgnoreCase).ToList()
            : AllowedTools.Count > 0 ? AllowedTools : other.AllowedTools;

        var mergedDeniedTools = DeniedTools.Union(other.DeniedTools, StringComparer.OrdinalIgnoreCase).ToList();
        // 默认效果：选择最严格的（Deny > Ask > Allow）
        // 注意：PermissionEffect 枚举值 Allow=0, Deny=1, Ask=2
        // 我们需要 Deny 优先，所以取最小值（0 最宽松，1 最严格对于 Deny/Allow）
        // 但 Ask=2 会干扰，所以需要自定义比较
        var mergedDefault = ChooseStrictDefault(DefaultEffect, other.DefaultEffect);

        return new AgentPermissionPolicy
        {
            AgentName = $"{AgentName}∩{other.AgentName}",
            Rules = mergedRules,
            AllowedTools = mergedAllowedTools,
            DeniedTools = mergedDeniedTools,
            AllowedAgents = AllowedAgents.Intersect(other.AllowedAgents).ToList(),
            AllowedMcpServers = AllowedMcpServers.Intersect(other.AllowedMcpServers).ToList(),
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
}
