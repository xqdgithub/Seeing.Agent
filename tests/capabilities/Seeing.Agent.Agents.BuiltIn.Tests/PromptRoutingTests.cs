using System.Text.RegularExpressions;
using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Xunit;

namespace Seeing.Agent.Agents.BuiltIn.Tests;

/// <summary>
/// 内置 Agent 的「## 工具选择」路由段约束：
/// 1) 四个 Agent 都有该段，且不再保留旧的工具段落；
/// 2) 段内反引号引用的工具 id 必须是该 Agent 可用的工具（或已知 Agent 名）。
/// </summary>
public class PromptRoutingTests
{
    private static readonly string[] s_allToolIds =
    [
        "read", "write", "edit", "delete", "grep", "glob", "open_file", "add_workspace_path", "bash",
        "task", "task_status", "todowrite",
        "session_list", "session_read", "session_search", "session_trim", "session_handoff",
        "session_inbox_send", "session_takeover", "session_release",
        "memory_search", "memory_read", "memory_write",
        "editor_query", "editor_list_commands", "editor_command",
        "cron_create", "cron_list", "cron_disable", "cron_resume", "cron_run", "cron_delete",
        "websearch", "webfetch", "codesearch", "skill", "tool_search",
        "git_status", "git_diff", "git_log", "git_commit",
        "send_channel_message", "list_channel_conversations", "list_channel_users",
        "acp", "acp_status",
        "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score",
        "current_time", "question",
    ];

    [Theory]
    [InlineData("build")]
    [InlineData("plan")]
    [InlineData("explore")]
    [InlineData("general")]
    public void Agent_ShouldHaveToolSelectionSection(string name)
    {
        var prompt = Agent(name).SystemPrompt;

        prompt.Should().Contain("## 工具选择");
        prompt.Should().NotContain("## Tools");
        prompt.Should().NotContain("## 工具使用");
        prompt.Should().NotContain("## 搜索策略");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("plan")]
    [InlineData("explore")]
    [InlineData("general")]
    public void RoutingSection_ShouldOnlyReferenceAvailableTools(string name)
    {
        var agent = Agent(name);
        var section = ExtractSection(agent.SystemPrompt, "## 工具选择");
        section.Should().NotBeNullOrWhiteSpace();
        var available = AvailableTools(agent);

        foreach (Match match in Regex.Matches(section, "`([^`]+)`"))
        {
            var token = match.Groups[1].Value;
            if (token.Contains('.') || token.Contains('/') || token.Contains('='))
                continue; // 路径、参数值
            if (!s_allToolIds.Contains(token))
                continue; // Agent 名等非工具 token

            available.Should().Contain(token, $"{name} 的路由段提到了不可用工具 {token}");
        }
    }

    private static AgentDefinition Agent(string name) =>
        BuiltInAgents.GetBuiltInAgents().Single(a => a.Name == name);

    /// <summary>可用工具集：白名单优先；否则用 Allow 规则（排除通配）；再剔除 DeniedTools。</summary>
    private static HashSet<string> AvailableTools(AgentDefinition agent)
    {
        if (agent.AllowedTools.Count > 0)
            return [.. agent.AllowedTools];

        var allowed = agent.PermissionRules
            .Where(r => r.Kind == PermissionKind.Tool && r.Effect == PermissionEffect.Allow)
            .Select(r => r.Pattern)
            .Where(p => p != "*");
        var set = new HashSet<string>(allowed);
        if (set.Count == 0)
            set = [.. s_allToolIds];

        foreach (var denied in agent.DeniedTools)
            set.Remove(denied);
        return set;
    }

    private static string ExtractSection(string prompt, string heading)
    {
        var lines = prompt.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == heading);
        if (start < 0)
            return string.Empty;

        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("## "))
            end++;

        return string.Join('\n', lines[start..end]);
    }
}
