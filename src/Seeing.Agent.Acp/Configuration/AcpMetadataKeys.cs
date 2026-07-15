using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 相关 Session Metadata 键名。
/// </summary>
public static class AcpMetadataKeys
{
    public const string PassthroughPrefix = "acp:passthrough:";
    public const string TaskPrefix = "acp:task:";

    /// <summary>AgentContext.Metadata：ACP session mode id（见 <see cref="AgentContextKeys.AcpModeId"/>）</summary>
    public const string ContextModeId = AgentContextKeys.AcpModeId;

    /// <summary>AgentContext.Metadata：ACP session model id（见 <see cref="AgentContextKeys.RequestModelId"/>）</summary>
    public const string ContextModelId = AgentContextKeys.RequestModelId;

    public static string Passthrough(string seeingSessionId) => PassthroughPrefix + seeingSessionId;

    public static string Task(string taskId) => TaskPrefix + taskId;
}
