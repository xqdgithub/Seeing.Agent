using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>SystemOne 配置变更重载处理器。</summary>
public sealed class SystemOneReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly SystemOneProviderManager _manager;

    /// <summary>注入 provider 管理器。</summary>
    public SystemOneReloadHandler(SystemOneProviderManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc />
    public override string ComponentId => "systemone";

    /// <inheritdoc />
    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0
            || change.ChangedSections.Contains(SystemOneConfigStore.SectionName))
        {
            _manager.Reload();
        }

        return Task.CompletedTask;
    }
}
