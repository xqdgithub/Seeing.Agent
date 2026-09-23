using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Core;

/// <summary>
/// 统一 Agent 默认解析，供 Gateway 与 WebUI 共用。
/// 执行路径由 Agent 的 <see cref="AgentRuntime"/> 自动分流 ACP / Native。
/// </summary>
public sealed class AgentSelectionResolver : IAgentSelectionResolver
{
    private readonly IAgentRuntimeManager _runtimeManager;

    /// <summary>
    /// 构造解析器，注入 Agent 运行时管理器以读取默认 Agent 配置。
    /// </summary>
    public AgentSelectionResolver(IAgentRuntimeManager runtimeManager)
    {
        _runtimeManager = runtimeManager;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public string? ResolveAcpModeId(string? requestModeId, string? sessionSelectedAcpMode)
    {
        if (!string.IsNullOrWhiteSpace(requestModeId))
            return requestModeId.Trim();

        if (!string.IsNullOrWhiteSpace(sessionSelectedAcpMode))
            return sessionSelectedAcpMode.Trim();

        return null;
    }
}
