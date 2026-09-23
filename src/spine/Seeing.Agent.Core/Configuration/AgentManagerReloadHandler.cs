using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 工作区切换重载处理器：重新发现并应用 Agent MD 配置
/// </summary>
public sealed class AgentManagerReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly AgentManager _manager;

    /// <summary>初始化工作区切换重载处理器，绑定目标 AgentManager。</summary>
    public AgentManagerReloadHandler(AgentManager manager) => _manager = manager;

    /// <summary>组件标识，用于按 agent-md 注册该重载处理器。</summary>
    public override string ComponentId => "agent-md";

    /// <summary>执行重载：重新发现并应用 Agent MD 配置。</summary>
    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
        => _manager.ReloadMdOverridesAsync(ct);
}
