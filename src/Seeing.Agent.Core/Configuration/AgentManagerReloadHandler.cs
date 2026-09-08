using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区切换重载处理器：重新发现并应用 Agent MD 配置
/// </summary>
public sealed class AgentManagerReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly AgentManager _manager;

    public AgentManagerReloadHandler(AgentManager manager) => _manager = manager;

    public override string ComponentId => "agent-md";

    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
        => _manager.ReloadMdOverridesAsync(ct);
}
