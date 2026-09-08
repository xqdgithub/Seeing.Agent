namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// 统一 Agent 默认解析，供 Gateway / Scheduler / Hosting 共用。
/// 执行路径由 Agent 的 <see cref="AgentRuntime"/> 自动分流 ACP / Native。
/// </summary>
public interface IAgentSelectionResolver
{
    /// <summary>解析最终使用的 Agent ID</summary>
    Task<string> ResolveAgentIdAsync(
        string? requestAgentId,
        string? sessionSelectedAgent,
        CancellationToken cancellationToken = default);

    /// <summary>解析 ACP 透传 session mode（request &gt; session &gt; null）。</summary>
    string? ResolveAcpModeId(string? requestModeId, string? sessionSelectedAcpMode);
}
