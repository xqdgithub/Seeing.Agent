using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Integration.Hosting;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryEvolutionWorker : BackgroundService
{
    public const string ModuleId = "memory";
    internal const string DeactivateSentinel = "__memory_module_deactivate__";

    private readonly IMemoryEvolutionService _evolution;
    private readonly ISessionActivityTracker _activity;
    private readonly IMemoryFlushService _flush;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly IMemorySessionEvents _sessionEvents;
    private readonly MemoryModuleActivity _moduleActivity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<MemoryEvolutionWorker> _logger;
    private readonly Channel<string> _sessionEndQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? _deactivateCts;

    public MemoryEvolutionWorker(
        IMemoryEvolutionService evolution,
        ISessionActivityTracker activity,
        IMemoryFlushService flush,
        IOptionsMonitor<MemoryOptions> options,
        IMemorySessionEvents sessionEvents,
        MemoryModuleActivity moduleActivity,
        ILogger<MemoryEvolutionWorker> logger,
        IModuleCatalog? catalog = null)
    {
        _evolution = evolution;
        _activity = activity;
        _flush = flush;
        _options = options;
        _sessionEvents = sessionEvents;
        _moduleActivity = moduleActivity;
        _logger = logger;
        _catalog = catalog;
        _moduleActivity.RegisterWake(() =>
        {
            _sessionEndQueue.Writer.TryWrite(DeactivateSentinel);
            try { _deactivateCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        });
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger.LogDebug("Memory module disabled; EvolutionWorker HostedService no-op");
            return Task.CompletedTask;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryEvolutionWorker started");

        using var subscription = _sessionEvents.SessionEnded.Subscribe(sessionId =>
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && _moduleActivity.IsActive)
                _sessionEndQueue.Writer.TryWrite(sessionId);
        });

        var idleLoop = IdleLoopAsync(stoppingToken);
        var endLoop = SessionEndLoopAsync(stoppingToken);
        await Task.WhenAll(idleLoop, endLoop);
    }

    private async Task IdleLoopAsync(CancellationToken stoppingToken)
    {
        using var deactivateCts = new CancellationTokenSource();
        _deactivateCts = deactivateCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deactivateCts.Token);

        while (!stoppingToken.IsCancellationRequested && _moduleActivity.IsActive)
        {
            try
            {
                var opts = _options.CurrentValue;
                if (opts.Enabled)
                {
                    // 缓冲空闲 flush（默认 5 分钟），与 Evolution 升格解耦
                    if (opts.Extraction.Enabled)
                        _flush.FlushIdleSessions();

                    if (opts.Evolution.Enabled)
                    {
                        var idle = TimeSpan.FromMinutes(Math.Max(1, opts.Evolution.IdleMinutes));
                        foreach (var sessionId in _activity.GetIdleSessions(idle))
                        {
                            if (!_moduleActivity.IsActive)
                                break;
                            if (opts.Extraction.Enabled)
                                await _flush.FlushSessionInlineAsync(sessionId, stoppingToken);
                            await _evolution.EvolveSessionAsync(sessionId, stoppingToken);
                            _activity.Clear(sessionId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || !_moduleActivity.IsActive)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MemoryEvolutionWorker idle loop error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || !_moduleActivity.IsActive)
            {
                break;
            }
        }
    }

    private async Task SessionEndLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var sessionId in _sessionEndQueue.Reader.ReadAllAsync(stoppingToken))
        {
            if (sessionId == DeactivateSentinel || !_moduleActivity.IsActive)
                break;

            try
            {
                var opts = _options.CurrentValue;
                if (!opts.Enabled)
                    continue;

                // 先同步 flush 提取，再 evolve 升格
                if (opts.Extraction.Enabled)
                    await _flush.FlushSessionInlineAsync(sessionId, stoppingToken);

                if (opts.Evolution.Enabled && opts.Evolution.OnSessionEnd)
                {
                    await _evolution.EvolveSessionAsync(sessionId, stoppingToken);
                    _logger.LogInformation("Evolved memory after session end: {SessionId}", sessionId);
                }

                _activity.Clear(sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session-end evolution failed for {SessionId}", sessionId);
            }
        }
    }
}
