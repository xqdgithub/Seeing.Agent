using Seeing.Agent.Abstractions.Agents;
namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 运行时辅助（枚举定义见 <see cref="Seeing.Agent.Abstractions.Agents.AgentRuntime"/>）。
/// </summary>
public static class AgentRuntimeExtensions
{
    public static bool IsPassthrough(this Seeing.Agent.Abstractions.Agents.AgentRuntime runtime) =>
        runtime == Seeing.Agent.Abstractions.Agents.AgentRuntime.AcpPassthrough;
}
