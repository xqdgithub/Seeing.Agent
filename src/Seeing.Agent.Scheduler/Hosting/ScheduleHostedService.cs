using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Configuration;

namespace Seeing.Agent.Scheduler.Hosting;

/// <summary>随 Generic Host 启停调度器</summary>
public sealed class ScheduleHostedService : IHostedService
{
    private readonly IScheduleManager _manager;
    private readonly SchedulerOptionsProvider _optionsProvider;
    private readonly ILogger<ScheduleHostedService> _logger;

    public ScheduleHostedService(
        IScheduleManager manager,
        SchedulerOptionsProvider optionsProvider,
        ILogger<ScheduleHostedService> logger)
    {
        _manager = manager;
        _optionsProvider = optionsProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _optionsProvider.Reload();
        if (!_optionsProvider.Current.Enabled)
        {
            _logger.LogDebug("Scheduler disabled, hosted service skipping start");
            return;
        }

        await _manager.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _manager.StopAsync(cancellationToken);
}
