using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Integration.Adapters;

/// <summary>
/// 默认 Agent 适配器 - 包装 AgentExecutor
/// </summary>
public class DefaultAgentAdapter : IAgentAdapter
{
    private readonly AgentExecutor _executor;
    private readonly IAgentRegistry _registry;
    private readonly IAgentRuntimeManager _runtimeManager;

    public DefaultAgentAdapter(
        AgentExecutor executor,
        IAgentRegistry registry,
        IAgentRuntimeManager runtimeManager)
    {
        _executor = executor;
        _registry = registry;
        _runtimeManager = runtimeManager;
    }

    public string? CurrentModel => _runtimeManager.CurrentModel;

    public async IAsyncEnumerable<IMessageEvent> ExecuteStreamAsync(
        AgentDefinition definition,
        AgentContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in _executor.ExecuteAsync(definition, context, cancellationToken))
        {
            yield return evt;
        }
    }

    public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync()
    {
        return await _registry.GetAgentsAsync();
    }

    public async Task<string> GetDefaultAgentNameAsync()
    {
        return await _registry.GetDefaultAgentNameAsync();
    }

    public async Task<ModelReference?> GetEffectiveModelAsync(string agentName)
    {
        return await _registry.GetEffectiveModelAsync(agentName);
    }

    public async Task SwitchAgentAsync(string agentName)
    {
        await _runtimeManager.SwitchAgentAsync(agentName);
    }

    public async Task SetSessionModelOverrideAsync(string agentName, string modelId)
    {
        await _runtimeManager.SetSessionModelOverrideAsync(agentName, modelId);
    }
}