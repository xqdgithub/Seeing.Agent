using Seeing.Agent.Commands;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Core.Commands.Impl;

/// <summary>
/// Agent 命令 - 列出和切换 Agent
/// </summary>
public class AgentCommand : Seeing.Agent.Commands.ICommand
{
    private readonly Core.TuiState _state;
    private readonly IAgentRegistry _registry;
    private readonly IAgentRuntimeManager _runtimeManager;

    public AgentCommand(Core.TuiState state, IAgentRegistry registry, IAgentRuntimeManager runtimeManager)
    {
        _state = state;
        _registry = registry;
        _runtimeManager = runtimeManager;
    }

    public CommandMetadata Metadata => new()
    {
        Name = "agent",
        Aliases = new[] { "agents" },
        Description = "切换或列出 Agent",
        Usage = "/agent [name] 或 /agents",
        Category = CommandCategory.Agent,
        SortOrder = 50
    };

    // 新接口执行入口由 ExecuteAsync(context, ct) 提供

    public async Task<CommandResult> ExecuteAsync(Seeing.Agent.Commands.CommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            return await ListAgentsAsync();
        }
        return await SwitchAgentAsync(context.Arguments.Trim());
    }

    private async Task<CommandResult> ListAgentsAsync()
    {
        var primary = await _registry.GetPrimaryAgentsAsync();
        var sub = await _registry.GetSubAgentsAsync();

        if (primary.Count == 0 && sub.Count == 0)
            return CommandResult.Ok("注册中心中无任何 Agent。");

        var lines = new List<string>();

        if (primary.Count > 0)
        {
            lines.Add("主代理（可作当前对话）:");
            lines.Add("");
            foreach (var a in primary)
            {
                var status = NameEqualsCurrent(a.Name) ? "✓ 当前" : "  就绪";
                var modelRef = await _registry.GetEffectiveModelAsync(a.Name);
                var model = modelRef?.ToString() ?? "未设置";
                lines.Add($"  {status} {a.Name} ({model})");
                lines.Add($"    模式 {a.Mode} · {a.Description ?? ""}");
            }
        }

        if (sub.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add("");
            lines.Add("子代理（委派/工具链用）:");
            lines.Add("");
            foreach (var a in sub)
            {
                var status = NameEqualsCurrent(a.Name) ? "✓ 当前" : "  就绪";
                var modelRef = await _registry.GetEffectiveModelAsync(a.Name);
                var model = modelRef?.ToString() ?? "未设置";
                lines.Add($"  {status} {a.Name} ({model})");
                lines.Add($"    模式 {a.Mode} · {a.Description ?? ""}");
            }
        }

        return CommandResult.Ok(string.Join("\n", lines));
    }

    private async Task<CommandResult> SwitchAgentAsync(string agentName)
    {
        var all = await _registry.GetAgentsAsync();
        var info = all.FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (info == null)
            return CommandResult.Fail($"注册中心中不存在: {agentName}", "使用 /agents 查看可用 Agent");

        if (info.IsHidden || !CanSelectAsChatAgent(info))
            return CommandResult.Fail($"不能将「{info.Name}」设为当前对话 Agent（已隐藏或模式不允许）。");

        await _runtimeManager.SwitchAgentAsync(info.Name);

        _state.CurrentAgentKey = info.Name;
        _state.CurrentModel = _runtimeManager.CurrentModel;

        return CommandResult.Ok($"已切换到 Agent: {info.Name}", true);
    }

    private bool NameEqualsCurrent(string name) =>
        string.Equals(name, _state.CurrentAgentKey, StringComparison.OrdinalIgnoreCase);

    private static bool CanSelectAsChatAgent(AgentInfo info) =>
        info.Mode is AgentMode.Primary or AgentMode.All or AgentMode.SubAgent;
}
