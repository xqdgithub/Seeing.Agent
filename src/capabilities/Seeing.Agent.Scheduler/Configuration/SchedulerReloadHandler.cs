using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>Scheduler 配置变更重载处理器</summary>
public sealed class SchedulerReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly SchedulerOptionsProvider _provider;

    public SchedulerReloadHandler(SchedulerOptionsProvider provider) => _provider = provider;

    public override string ComponentId => "scheduler";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains("Scheduler"))
            _provider.Reload();
        return Task.CompletedTask;
    }
}
