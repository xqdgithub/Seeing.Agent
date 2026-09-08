using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Prompts;
using System.Text;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// Core 内置代理分节贡献者 — 列出可供委托的子代理。
/// </summary>
public sealed class AgentsPromptSectionContributor : IPromptSectionContributor
{
    private readonly IAgentRegistry _agentRegistry;

    public AgentsPromptSectionContributor(IAgentRegistry agentRegistry)
    {
        _agentRegistry = agentRegistry;
    }

    /// <inheritdoc />
    public string SectionName => PromptSectionNames.Agents;

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public async Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        IEnumerable<AgentDefinition> agents;
        if (context.Agents != null)
        {
            agents = context.Agents;
        }
        else
        {
            agents = await _agentRegistry.GetAgentsAsync().ConfigureAwait(false);
        }

        return BuildAgentSection(agents, context.Agent?.Name);
    }

    internal static string BuildAgentSection(IEnumerable<AgentDefinition> agents, string? currentAgentName)
    {
        var agentList = agents.ToList();
        var subAgents = agentList
            .Where(a => a.Mode == AgentMode.SubAgent && a.Name != currentAgentName)
            .ToList();

        if (subAgents.Count == 0)
            return "暂无可用子代理。";

        var sb = new StringBuilder();
        sb.AppendLine("以下代理可供委托：");
        sb.AppendLine();

        foreach (var agent in subAgents)
        {
            var desc = agent.Description ?? "无描述";
            var shortDesc = desc.Split('.')[0];
            sb.AppendLine($"- **{agent.Name}**: {shortDesc}");
        }

        return sb.ToString().TrimEnd();
    }
}
