using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Transport;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 定期回收超过 <see cref="AcpOptions.IdleTimeout"/> 未使用的 ACP 子进程租约。
/// </summary>
internal sealed class AcpConnectionIdleCleanupHostedService : BackgroundService, IModuleHostedService
{
    public const string ModuleIdValue = "acp";
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    internal const string DeactivateSentinel = "__acp_module_deactivate__";

    private readonly AcpConnectionOwner _connectionOwner;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly IModuleCatalog? _catalog;
    private readonly AcpModuleActivity _activity;
    private readonly ILogger<AcpConnectionIdleCleanupHostedService> _logger;
    private readonly ModuleHostedRunGate _run = new();
    // 循环中每轮都会挂起一个 ReadAsync；当计时器获胜时旧读取仍处于 pending，
    // 下一轮会产生新的读取，故不能声明 SingleReader（否则违反通道契约）。
    private readonly System.Threading.Channels.Channel<string> _wake =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = false });

    public AcpConnectionIdleCleanupHostedService(
        AcpConnectionOwner connectionOwner,
        IOptionsMonitor<AcpOptions> options,
        AcpModuleActivity activity,
        ILogger<AcpConnectionIdleCleanupHostedService> logger,
        IModuleCatalog? catalog = null)
    {
        _connectionOwner = connectionOwner;
        _options = options;
        _activity = activity;
        _logger = logger;
        _catalog = catalog;
        _activity.RegisterWake(() => _wake.Writer.TryWrite(DeactivateSentinel));
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning)
            return Task.CompletedTask;

        if (_catalog is not null && !_catalog.IsEnabled(ModuleIdValue))
        {
            _logger.LogDebug("ACP module disabled; idle cleanup HostedService no-op");
            return Task.CompletedTask;
        }

        if (!_run.TryBegin())
            return Task.CompletedTask;

        try
        {
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            _run.End();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.End();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.CurrentValue.Enabled)
                return;

            if (!_activity.IsActive && _catalog is not null && !_catalog.IsEnabled(ModuleIdValue))
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

                    await _connectionOwner.Manager.EvictIdleLeasesAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }

            _logger.LogDebug("ACP idle lease cleanup stopped");
        }
        finally
        {
            _run.End();
        }
    }
}
