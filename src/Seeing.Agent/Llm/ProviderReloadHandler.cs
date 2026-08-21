using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>Provider 配置变更重载处理器</summary>
public sealed class ProviderReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly ProviderManager _manager;

    public ProviderReloadHandler(ProviderManager manager) => _manager = manager;

    /// <inheritdoc/>
    public override string ComponentId => "provider";

    /// <inheritdoc/>
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("Providers"))
            _manager.RefreshConfiguredProviders();
        return Task.CompletedTask;
    }
}
