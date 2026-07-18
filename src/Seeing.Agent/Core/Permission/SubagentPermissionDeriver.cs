using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 对齐 OpenCode deriveSubagentSessionPermission：派生子 Agent 会话权限快照。
/// </summary>
public static class SubagentPermissionDeriver
{
    private static readonly HashSet<string> s_editToolPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "write"
    };

    /// <summary>
    /// 派生 Child Session 权限快照（不做 AllowedTools 全量交集）。
    /// </summary>
    public static IReadOnlyList<SessionPermissionRule> Derive(
        IReadOnlyList<SessionPermissionRule> parentSessionRules,
        AgentDefinition? parentAgent,
        AgentDefinition subagent)
    {
        ArgumentNullException.ThrowIfNull(subagent);

        var result = new List<SessionPermissionRule>();

        // 1. 父 Agent 的 edit/write 类 Deny
        if (parentAgent?.PermissionRules != null)
        {
            foreach (var rule in parentAgent.PermissionRules)
            {
                if (rule.Effect != PermissionEffect.Deny)
                    continue;
                if (rule.Kind != PermissionKind.Tool && rule.Kind != PermissionKind.File)
                    continue;
                if (rule.Kind == PermissionKind.Tool && !IsEditClassPattern(rule.Pattern))
                    continue;
                result.Add(SessionPermissionMapper.ToSessionRule(rule));
            }
        }

        // 2. 父 Session 的 Deny + 目录类限制
        foreach (var rule in parentSessionRules ?? Array.Empty<SessionPermissionRule>())
        {
            if (string.Equals(rule.Effect, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Clone(rule));
                continue;
            }

            if (IsDirectoryClassRule(rule))
                result.Add(Clone(rule));
        }

        // 3–4. 默认 deny todowrite / task（子未显式 Allow 时）
        if (!AllowsTool(subagent, "todowrite"))
        {
            result.Add(new SessionPermissionRule
            {
                Kind = nameof(PermissionKind.Tool),
                Pattern = "todowrite",
                Effect = nameof(PermissionEffect.Deny),
                Priority = 100
            });
        }

        if (!AllowsTool(subagent, "task"))
        {
            result.Add(new SessionPermissionRule
            {
                Kind = nameof(PermissionKind.Tool),
                Pattern = "task",
                Effect = nameof(PermissionEffect.Deny),
                Priority = 100
            });
        }

        return result;
    }

    private static bool IsEditClassPattern(string pattern) =>
        s_editToolPatterns.Contains(pattern) ||
        string.Equals(pattern, "*", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectoryClassRule(SessionPermissionRule rule)
    {
        if (!string.Equals(rule.Kind, nameof(PermissionKind.File), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rule.Kind, "external_directory", StringComparison.OrdinalIgnoreCase))
            return false;

        return rule.Pattern.Contains('/') ||
               rule.Pattern.Contains('\\') ||
               rule.Pattern.Contains("**") ||
               string.Equals(rule.Kind, "external_directory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsTool(AgentDefinition agent, string toolId)
    {
        foreach (var rule in agent.PermissionRules)
        {
            if (rule.Kind != PermissionKind.Tool || rule.Effect != PermissionEffect.Allow)
                continue;
            if (rule.Pattern == "*" ||
                string.Equals(rule.Pattern, toolId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static SessionPermissionRule Clone(SessionPermissionRule r) => new()
    {
        Kind = r.Kind,
        Pattern = r.Pattern,
        Effect = r.Effect,
        Priority = r.Priority
    };
}
