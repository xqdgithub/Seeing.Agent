using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Agents.BuiltIn;

/// <summary>
/// 内置 Agent 模块 — 提供 build/plan/explore/general/summary 定义。
/// </summary>
public sealed class AgentsBuiltInModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["agents"];

    private readonly IAgentStore? _agentStore;

    /// <summary>无 store 的实例仅用于 ConfigureServices（登记 DI）。</summary>
    public AgentsBuiltInModule() : this(null)
    {
    }

    /// <summary>Activate 阶段注入的 store，用于两阶段注册。</summary>
    public AgentsBuiltInModule(IAgentStore? agentStore)
    {
        _agentStore = agentStore;
    }

    /// <inheritdoc />
    public string Id => "agents.builtin";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // Built-in AgentDefinition 改由 ActivateAsync → IAgentStore.RegisterAsync 注册（两阶段）。
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_agentStore is null)
        {
            throw new InvalidOperationException(
                "AgentsBuiltInModule.ActivateAsync requires IAgentStore. Resolve the module via DI with IAgentStore.");
        }

        foreach (var agent in BuiltInAgents.GetBuiltInAgents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _agentStore.RegisterAsync(agent);
        }
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
