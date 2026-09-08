using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 定期回收超过 <see cref="SeeingAgentOptions.Acp.IdleTimeout"/> 未使用的 ACP 子进程租约。
/// </summary>
internal sealed class AcpConnectionIdleCleanupHostedService : BackgroundService
{
    public const string ModuleId = "acp";
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    internal const string DeactivateSentinel = "__acp_module_deactivate__";

    private readonly AcpConnectionManager _connectionManager;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly IModuleCatalog? _catalog;
    private readonly AcpModuleActivity _activity;
    private readonly ILogger<AcpConnectionIdleCleanupHostedService> _logger;
    private readonly System.Threading.Channels.Channel<string> _wake =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    public AcpConnectionIdleCleanupHostedService(
        AcpConnectionManager connectionManager,
        IOptionsMonitor<AcpOptions> options,
        AcpModuleActivity activity,
        ILogger<AcpConnectionIdleCleanupHostedService> logger,
        IModuleCatalog? catalog = null)
    {
        _connectionManager = connectionManager;
        _options = options;
        _activity = activity;
        _logger = logger;
        _catalog = catalog;
        _activity.RegisterWake(() => _wake.Writer.TryWrite(DeactivateSentinel));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger.LogDebug("ACP module disabled; idle cleanup HostedService no-op");
            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
            return;

        if (!_activity.IsActive && _catalog is not null && !_catalog.IsEnabled(ModuleId))
            return;

        _logger.LogDebug(
            "ACP idle lease cleanup started (interval={Interval}, idleTimeout={IdleTimeout})",
            CleanupInterval,
            _options.CurrentValue.IdleTimeout);

        using var timer = new PeriodicTimer(CleanupInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested && _activity.IsActive)
            {
                var timerTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                var wakeTask = _wake.Reader.ReadAsync(stoppingToken).AsTask();
                var completed = await Task.WhenAny(timerTask, wakeTask).ConfigureAwait(false);

                if (completed == wakeTask)
                {
                    _ = await wakeTask.ConfigureAwait(false);
                    if (!_activity.IsActive)
                        break;
                    continue;
                }

                if (!await timerTask.ConfigureAwait(false))
                    break;

                if (!_activity.IsActive)
                    break;

                await _connectionManager.EvictIdleLeasesAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _logger.LogDebug("ACP idle lease cleanup stopped");
    }
}
