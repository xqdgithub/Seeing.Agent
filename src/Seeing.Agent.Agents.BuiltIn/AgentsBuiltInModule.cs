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
        // Interim (pre Host Shape / Phase 5 two-phase AgentManager):
        // register definitions so AgentManager can resolve IEnumerable via GetServices.
        // ActivateAsync will RegisterAsync on IAgentStore once the host supplies a scope.
        foreach (var agent in BuiltInAgents.GetBuiltInAgents())
        {
            services.AddSingleton(agent);
        }
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
