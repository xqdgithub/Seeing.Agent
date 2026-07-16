using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.App.Commands;

/// <summary>
/// Agent 命令提供者 - 提供 Agent 和模型切换命令
/// </summary>
[CommandProvider]
public class AgentCommands
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly ILlmService _llmService;

    public AgentCommands(
        IAgentRegistry agentRegistry,
        ILlmService llmService)
    {
        _agentRegistry = agentRegistry;
        _llmService = llmService;
    }

    /// <summary>
    /// /agent - 切换或显示 Agent
    /// </summary>
    [Command(
        "切换或显示当前 Agent",
        Name = "agent",
        Usage = "/agent [name]",
        Category = CommandCategory.Agent,
        Aliases = new[] { "ag" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public async Task<CommandResult> SwitchAgent(CommandContext context, CancellationToken ct = default)
    {
        var agents = await _agentRegistry.GetPrimaryAgentsAsync();
        var agentList = agents.Where(a => !string.IsNullOrEmpty(a.Name)).ToList();

        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            // 显示可用 Agent 列表
            var list = "**Available Agents**\n\n";
            foreach (var agent in agentList)
            {
                list += $"- **{agent.Name}**: {agent.Description ?? ""}\n";
            }
            list += "\nUse `/agent <name>` to switch.";
            return CommandResult.Ok(list);
        }

        // 切换 Agent
        var targetName = context.Arguments.Trim();
        var targetAgent = agentList.FirstOrDefault(a =>
            string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (targetAgent == null)
        {
            return CommandResult.Fail($"Agent not found: {targetName}");
        }

        // 执行切换（持久化）
        await _agentRegistry.SetDefaultAgentAsync(targetAgent.Name);

        return CommandResult.WithData($"Switched to agent: {targetAgent.Name}", 
            new Dictionary<string, object> { ["agentSwitch"] = targetAgent.Name });
    }

    /// <summary>
    /// /model - 切换或显示模型
    /// </summary>
    [Command(
        "切换或显示当前模型",
        Name = "model",
        Usage = "/model [name]",
        Category = CommandCategory.Agent,
        Aliases = new[] { "mdl" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public CommandResult SwitchModel(CommandContext context)
    {
        var models = _llmService.GetAvailableModels();

        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            // 显示可用模型列表
            var list = "**Available Models**\n\n";
            foreach (var model in models)
            {
                list += $"- **{model.Key}**: {model.Value?.Name ?? model.Key}\n";
            }
            list += "\nUse `/model <name>` to switch.";
            return CommandResult.Ok(list);
        }

        // 切换模型（前端处理实际切换）
        var targetName = context.Arguments.Trim();
        if (!models.ContainsKey(targetName))
        {
            return CommandResult.Fail($"Model not found: {targetName}");
        }

        return CommandResult.WithData($"Switched to model: {targetName}", 
            new Dictionary<string, object> { ["modelSwitch"] = targetName });
    }
}