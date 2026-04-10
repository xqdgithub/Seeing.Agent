using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tui.Core;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// 将 Skill / Tool / MCP 等「运行时真实状态」同步到 <see cref="TuiState"/>，供状态栏与命令使用。
/// </summary>
public static class TuiRuntimeSnapshot
{
    /// <summary>
    /// 从当前 DI 容器中的管理器刷新 TUI 状态中的计数与列表。
    /// </summary>
    public static async Task RefreshAsync(IServiceProvider provider, TuiState state)
    {
        var skills = provider.GetRequiredService<SkillManager>();
        var invoker = provider.GetRequiredService<ToolInvoker>();
        var mcp = provider.GetRequiredService<McpClientManager>();
        var registry = provider.GetRequiredService<IAgentRegistry>();

        state.SkillInfos = skills.GetAllSkillInfos();
        state.SkillCount = state.SkillInfos.Count;

        var tools = invoker.GetTools();
        state.ToolInfos = tools;
        state.ToolCount = tools.Count;

        var mcpToolList = mcp.GetTools().ToList();
        state.McpToolInfos = mcpToolList;
        state.McpServerNames = mcp.GetConnectedServers().ToList();
        state.McpServerCount = state.McpServerNames.Count;

        state.RegisteredMcpToolIds.Clear();
        var mcpIds = new HashSet<string>(mcpToolList.Select(t => t.Id), StringComparer.Ordinal);
        foreach (var t in tools)
        {
            if (mcpIds.Contains(t.Id))
                state.RegisteredMcpToolIds.Add(t.Id);
        }

        state.CurrentModel = (await registry.GetEffectiveModelAsync(state.CurrentAgentKey))?.ToString();

        await Task.CompletedTask;
    }
}
