using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供 Agent 实现的扩展
/// </summary>
public interface IAgentExtension
{
    /// <summary>提供的 Agent 实现</summary>
    IEnumerable<AgentDefinition> GetAgents();
}