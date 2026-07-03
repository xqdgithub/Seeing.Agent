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

        if (_options.Value.Agents.TryGetValue(agentName, out var agentConfig) &&
            !string.IsNullOrEmpty(agentConfig.Model))
        {
            return agentConfig.Model;
        }

        return null;
    }
}
