using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core.Llm;

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
        if (change.ChangedSections.Count == 0)
        {
            _manager.EnqueueRefresh("configuration");
            return Task.CompletedTask;
        }

        if (!change.ChangedSections.Contains("Providers"))
            return Task.CompletedTask;

        // Providers 节带作用域 → 仅刷新目标 Provider；无作用域 → 回退全量
        if (change.ChangedKeys.Count == 0)
        {
            _manager.EnqueueRefresh("configuration");
            return Task.CompletedTask;
        }

        foreach (var providerId in change.ChangedKeys)
            _manager.EnqueueProviderRefresh("configuration", providerId);

        return Task.CompletedTask;
    }
}
