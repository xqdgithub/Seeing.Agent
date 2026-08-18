using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Core;

/// <summary>
/// 统一 Agent 默认解析，供 Gateway 与 WebUI 共用。
/// 执行路径由 Agent 的 <see cref="Seeing.Agent.Abstractions.Agents.AgentRuntime"/> 自动分流 ACP / Native。
/// </summary>
public sealed class AgentSelectionResolver
{
    private readonly IAgentRuntimeManager _runtimeManager;

    public AgentSelectionResolver(IAgentRuntimeManager runtimeManager)
    {
        _runtimeManager = runtimeManager;
    }

    /// <summary>解析最终使用的 Agent ID</summary>
    public async Task<string> ResolveAgentIdAsync(
        string? requestAgentId,
        string? sessionSelectedAgent,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(requestAgentId))
            return requestAgentId;

        if (!string.IsNullOrEmpty(sessionSelectedAgent))
            return sessionSelectedAgent;

        cancellationToken.ThrowIfCancellationRequested();
        return await _runtimeManager.GetDefaultAgentNameAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("未配置默认 Agent（DefaultAgent）");
    }

    /// <summary>解析 ACP 透传 session mode（request &gt; session &gt; null）。</summary>
    public string? ResolveAcpModeId(string? requestModeId, string? sessionSelectedAcpMode)
    {
        if (!string.IsNullOrWhiteSpace(requestModeId))
            return requestModeId.Trim();

        if (!string.IsNullOrWhiteSpace(sessionSelectedAcpMode))
            return sessionSelectedAcpMode.Trim();

        return null;
    }
}
