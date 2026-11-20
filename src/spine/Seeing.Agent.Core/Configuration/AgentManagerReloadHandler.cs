using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 工作区切换重载处理器：清掉旧工作区的项目级 Agent 覆盖后按新工作区重建
/// </summary>
public sealed class AgentManagerReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly AgentManager _manager;

    /// <summary>初始化工作区切换重载处理器，绑定目标 AgentManager。</summary>
    public AgentManagerReloadHandler(AgentManager manager) => _manager = manager;

    /// <summary>组件标识，用于按 agent-md 注册该重载处理器。</summary>
    public override string ComponentId => "agent-md";

    /// <summary>执行重载：按新工作区的项目级配置从零重建（旧项目级覆盖先回收）。</summary>
    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
        => _manager.ResetProjectOverridesAsync(ct);
}
