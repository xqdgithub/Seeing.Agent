using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>模型目录配置变更重载处理器</summary>
public sealed class ModelReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ModelConfigManager _manager;

    public ModelReloadHandler(ModelConfigManager manager) => _manager = manager;

    /// <inheritdoc />
    public override string ComponentId => "model-catalog";

    /// <inheritdoc />
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("Providers"))
            _manager.EnqueueRefresh("configuration");
        return Task.CompletedTask;
    }
}
