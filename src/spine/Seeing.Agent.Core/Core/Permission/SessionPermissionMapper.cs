using Seeing.Session.Core;

using Seeing.Agent.Abstractions.Permissions;
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// Agent PermissionRuleEntry ↔ Session PermissionSnapshot DTO
/// </summary>
public static class SessionPermissionMapper
{
    /// <summary>
    /// 将 Agent 权限规则映射为 Session 快照 DTO（枚举转为字符串）。
    /// </summary>
    public static SessionPermissionRule ToSessionRule(PermissionRuleEntry rule) => new()
    {
        Kind = rule.Kind.ToString(),
        Pattern = rule.Pattern,
        Effect = rule.Effect.ToString(),
        Priority = rule.Priority
    };

    /// <summary>
    /// 将 Session 快照 DTO 还原为 Agent 权限规则，枚举解析失败时回退默认值。
    /// </summary>
    public static PermissionRuleEntry ToAgentRule(SessionPermissionRule rule)
    {
        if (!Enum.TryParse<PermissionKind>(rule.Kind, ignoreCase: true, out var kind))
            kind = PermissionKind.Tool;
        if (!Enum.TryParse<PermissionEffect>(rule.Effect, ignoreCase: true, out var effect))
            effect = PermissionEffect.Deny;

        return new PermissionRuleEntry
        {
            Kind = kind,
            Pattern = rule.Pattern,
            Effect = effect,
            Priority = rule.Priority,
            Source = "session-snapshot"
        };
    }

    /// <summary>
    /// 批量将 Session 快照 DTO 列表还原为 Agent 权限规则列表。
    /// </summary>
    public static IReadOnlyList<PermissionRuleEntry> ToAgentRules(
        IEnumerable<SessionPermissionRule> rules) =>
        rules.Select(ToAgentRule).ToList();

    /// <summary>
    /// 将 Child Session 权限快照合并进 Agent 策略（快照 Deny 优先生效）。
    /// </summary>
    public static AgentPermissionPolicy ApplySnapshot(
        AgentPermissionPolicy basePolicy,
        IReadOnlyList<SessionPermissionRule>? snapshot)
    {
        ArgumentNullException.ThrowIfNull(basePolicy);
        if (snapshot == null || snapshot.Count == 0)
            return basePolicy;

        return basePolicy.WithSessionSnapshot(ToAgentRules(snapshot));
    }
}
