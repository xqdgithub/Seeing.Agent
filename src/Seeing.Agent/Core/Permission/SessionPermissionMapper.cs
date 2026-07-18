using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// Agent PermissionRuleEntry ↔ Session PermissionSnapshot DTO
/// </summary>
public static class SessionPermissionMapper
{
    public static SessionPermissionRule ToSessionRule(PermissionRuleEntry rule) => new()
    {
        Kind = rule.Kind.ToString(),
        Pattern = rule.Pattern,
        Effect = rule.Effect.ToString(),
        Priority = rule.Priority
    };

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
