using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core;

/// <summary>Agent 运行时配置重载处理器：DefaultAgent/AgentModels 变更 + 全量重载</summary>
public sealed class AgentRuntimeReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly AgentRuntimeManager _manager;

    public AgentRuntimeReloadHandler(AgentRuntimeManager manager) => _manager = manager;

    /// <inheritdoc/>
    public override string ComponentId => "agent-runtime";

    /// <inheritdoc/>
    protected override async Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        // 全量重载（ChangedSections 为空）或涉及 AgentModels 时重新应用模型绑定
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("AgentModels"))
        {
            await _manager.ApplyAgentModelsAsync();
        }

        // 默认 Agent 变更（原日志行为保留）
        if (change.ChangedSections.Count > 0 && change.ChangedSections.Contains("DefaultAgent"))
        {
        }
    }
}
