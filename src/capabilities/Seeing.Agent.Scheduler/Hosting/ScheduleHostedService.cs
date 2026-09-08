using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;

namespace Seeing.Agent.Scheduler.Hosting;

/// <summary>随 Generic Host 启停调度器；支持模块 Deactivate 后再 Activate 复活。</summary>
public sealed class ScheduleHostedService : IHostedService, IModuleHostedService
{
    public const string ModuleIdValue = "scheduler";

    private readonly IScheduleManager _manager;
    private readonly QuartzSchedulerEngine _engine;
    private readonly ISchedulerOptionsProvider _optionsProvider;
    private readonly SchedulerModuleActivity _activity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<ScheduleHostedService> _logger;
    private readonly ModuleHostedRunGate _run = new();

    public ScheduleHostedService(
        IScheduleManager manager,
        QuartzSchedulerEngine engine,
        ISchedulerOptionsProvider optionsProvider,
        SchedulerModuleActivity activity,
        ILogger<ScheduleHostedService> logger,
        IModuleCatalog? catalog = null)
    {
        _manager = manager;
        _engine = engine;
        _optionsProvider = optionsProvider;
        _activity = activity;
        _logger = logger;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning)
            return;

        if (_catalog is not null && !_catalog.IsEnabled(ModuleIdValue))
        {
            _logger.LogDebug("Scheduler module disabled, hosted service skipping start");
            return;
        }

        _optionsProvider.Reload();
        var options = _optionsProvider.Current;

        if (!options.Enabled)
        {
            _logger.LogDebug("Scheduler disabled, hosted service skipping start");
            return;
        }

        if (!_activity.IsActive && _catalog is not null)
        {
            _logger.LogDebug("Scheduler module not activated, hosted service skipping start");
            return;
        }

        if (!_run.TryBegin())
            return;

        try
        {
            _logger.LogInformation("Starting scheduler service...");
            await _manager.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _run.End();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _manager.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Scheduler stop canceled by host shutdown deadline");
        }
        finally
        {
            _run.End();
        }
    }
}
