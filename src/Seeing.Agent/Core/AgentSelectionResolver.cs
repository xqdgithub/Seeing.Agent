using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core;

/// <summary>
/// 统一 Agent / Model 默认解析，供 Gateway 与 WebUI 共用。
/// 执行路径由 Agent 的 <see cref="Models.AgentRuntime"/> 自动分流 ACP / Native。
/// </summary>
public sealed class AgentSelectionResolver
{
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly IAgentRegistry _agentRegistry;

    public AgentSelectionResolver(
        IOptions<SeeingAgentOptions> options,
        IAgentRegistry agentRegistry)
    {
        _options = options;
        _agentRegistry = agentRegistry;
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
        return await _agentRegistry.GetDefaultAgentNameAsync().ConfigureAwait(false);
    }

    /// <summary>解析 Native Agent 使用的模型 ID</summary>
    public string? ResolveModelId(string? requestModelId, string? sessionSelectedModel, string agentName)
    {
        if (!string.IsNullOrEmpty(requestModelId))
            return requestModelId;

        if (!string.IsNullOrEmpty(sessionSelectedModel))
            return sessionSelectedModel;

        if (!string.IsNullOrEmpty(_options.Value.DefaultModel))
            return _options.Value.DefaultModel;

        // 从 Agent 注册表获取 Agent 的默认模型
        var agent = _agentRegistry.GetAgentAsync(agentName).GetAwaiter().GetResult();
        if (agent?.Model?.ModelId != null)
        {
            return agent.Model.ModelId;
        }

        return null;
    }

    /// <summary>
    /// 解析 ACP 透传使用的模型 ID（不回退 <see cref="SeeingAgentOptions.DefaultModel"/> 或 Agent.Model）。
    /// </summary>
    public string? ResolveAcpModelId(string? requestModelId, string? sessionSelectedModel)
    {
        if (!string.IsNullOrEmpty(requestModelId))
            return requestModelId;

        if (!string.IsNullOrEmpty(sessionSelectedModel))
            return sessionSelectedModel;

        return null;
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
