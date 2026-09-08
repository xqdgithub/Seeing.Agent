using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Memory.Configuration;

/// <summary>Memory 配置变更重载处理器</summary>
public sealed class MemoryReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly MemoryOptionsProvider _provider;

    public MemoryReloadHandler(MemoryOptionsProvider provider) => _provider = provider;

    /// <inheritdoc/>
    public override string ComponentId => "memory";

    /// <inheritdoc/>
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("Memory"))
            _provider.Reload();
        return Task.CompletedTask;
    }
}
