using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryEvolutionWorker : BackgroundService
{
    private readonly IMemoryEvolutionService _evolution;
    private readonly ISessionActivityTracker _activity;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly IMemorySessionEvents _sessionEvents;
    private readonly ILogger<MemoryEvolutionWorker> _logger;
    private readonly Channel<string> _sessionEndQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public MemoryEvolutionWorker(
        IMemoryEvolutionService evolution,
        ISessionActivityTracker activity,
        IOptionsMonitor<MemoryOptions> options,
        IMemorySessionEvents sessionEvents,
        ILogger<MemoryEvolutionWorker> logger)
    {
        _evolution = evolution;
        _activity = activity;
        _options = options;
        _sessionEvents = sessionEvents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryEvolutionWorker started");

        using var subscription = _sessionEvents.SessionEnded.Subscribe(sessionId =>
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                _sessionEndQueue.Writer.TryWrite(sessionId);
        });

        var idleLoop = IdleLoopAsync(stoppingToken);
        var endLoop = SessionEndLoopAsync(stoppingToken);
        await Task.WhenAll(idleLoop, endLoop);
    }

    private async Task IdleLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                if (opts.Enabled && opts.Evolution.Enabled)
                {
                    var idle = TimeSpan.FromMinutes(Math.Max(1, opts.Evolution.IdleMinutes));
                    foreach (var sessionId in _activity.GetIdleSessions(idle))
                    {
                        await _evolution.EvolveSessionAsync(sessionId, stoppingToken);
                        _activity.Clear(sessionId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MemoryEvolutionWorker idle loop error");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task SessionEndLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var sessionId in _sessionEndQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var opts = _options.CurrentValue;
                if (!opts.Enabled || !opts.Evolution.Enabled || !opts.Evolution.OnSessionEnd)
                    continue;

                await _evolution.EvolveSessionAsync(sessionId, stoppingToken);
                _activity.Clear(sessionId);
                _logger.LogInformation("Evolved memory after session end: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session-end evolution failed for {SessionId}", sessionId);
            }
        }
    }
}
