namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 运行时辅助（枚举定义见 <see cref="Seeing.Agent.Core.Models.AgentRuntime"/>）。
/// </summary>
public static class AgentRuntimeExtensions
{
    public static bool IsPassthrough(this Seeing.Agent.Core.Models.AgentRuntime runtime) =>
        runtime == Seeing.Agent.Core.Models.AgentRuntime.AcpPassthrough;
}
