using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;

namespace Seeing.Agent.Scheduler.Hosting;

/// <summary>随 Generic Host 启停调度器</summary>
public sealed class ScheduleHostedService : IHostedService
{
    private readonly IScheduleManager _manager;
    private readonly QuartzSchedulerEngine _engine;
    private readonly ISchedulerOptionsProvider _optionsProvider;
    private readonly ILogger<ScheduleHostedService> _logger;

    public ScheduleHostedService(
        IScheduleManager manager,
        QuartzSchedulerEngine engine,
        ISchedulerOptionsProvider optionsProvider,
        ILogger<ScheduleHostedService> logger)
    {
        _manager = manager;
        _engine = engine;
        _optionsProvider = optionsProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _optionsProvider.Reload();
        var options = _optionsProvider.Current;
        
        if (!options.Enabled)
        {
            _logger.LogDebug("Scheduler disabled, hosted service skipping start");
            return;
        }

        _logger.LogInformation("Starting scheduler service...");
        await _manager.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _manager.StopAsync(cancellationToken);
}