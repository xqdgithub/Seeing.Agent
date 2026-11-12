using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;
using Seeing.Session.Management;
using System.Threading.Channels;

namespace Seeing.Agent.Memory.Background;

public sealed class MemoryEvolutionWorker : BackgroundService, IModuleHostedService
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
    private readonly ModuleHostedRunGate _run = new();

    /// <summary>
    /// 本工作者自己的「这段空闲期已处理」登记。
    /// <para>
    /// <b>不得改用 <see cref="ISessionActivityTracker.Clear"/></b>：追踪器存的是全进程共享的
    /// <b>事实</b>（最后活动时刻），记忆演化默认 15 分钟就处理完一轮、复盘默认等 30 分钟。
    /// 若在这里清掉时间戳，复盘的 <c>GetIdleSessions(30min)</c> 永远拿不到该会话——
    /// 空闲触发会<b>永久失效</b>，且没有任何报错（实测已发生）。
    /// </para>
    /// </summary>
    private readonly IdlePeriodClaimStore _idleClaims = new();
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

    /// <inheritdoc cref="IModuleHostedService.ModuleId" />
    string IModuleHostedService.ModuleId => ModuleId;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning)
            return Task.CompletedTask;

        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
        {
            _logger.LogDebug("Memory module disabled; EvolutionWorker HostedService no-op");
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
        finally
        {
            _run.End();
        }
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

                        // 二次确认仍然空闲，并取出本段空闲期的活动时刻（作为去重键）。
                        if (!_activity.TryGetLastActivity(sessionId, out var lastActivity))
                            continue;
                        if (DateTimeOffset.UtcNow - lastActivity < idle)
                            continue;

                        // 同段空闲期只演化一次：用户回来后 Touch 会换时刻，天然允许下一次。
                        if (_idleClaims.IsClaimed(sessionId, lastActivity))
                            continue;

                        if (opts.Extraction.Enabled)
                            await _flush.FlushSessionInlineAsync(sessionId, stoppingToken);
                        await _evolution.EvolveSessionAsync(sessionId, stoppingToken);

                        // 只登记去重，绝不清除共享的活跃度事实（见 _idleClaims 的注释）。
                        _idleClaims.Claim(sessionId, lastActivity);
                    }

                    // 会话被删除或已重新活动：清掉失效登记，避免字典随会话数无界增长。
                    _idleClaims.Prune(id =>
                        _activity.TryGetLastActivity(id, out var last) ? last : null);
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

                // 先同步 flush 提取，再 evolve 升格
                if (opts.Extraction.Enabled)
                    await _flush.FlushSessionInlineAsync(sessionId, stoppingToken);

                if (opts.Evolution.Enabled && opts.Evolution.OnSessionEnd)
                {
                    await _evolution.EvolveSessionAsync(sessionId, stoppingToken);
                    _logger.LogInformation("Evolved memory after session end: {SessionId}", sessionId);
                }

                // 同样只登记本工作者的去重状态，不清共享活跃度事实：
                // 会话结束不等于会话消失，复盘仍可能要对它做一次空闲复盘。
                if (_activity.TryGetLastActivity(sessionId, out var lastActivity))
                    _idleClaims.Claim(sessionId, lastActivity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session-end evolution failed for {SessionId}", sessionId);
            }
        }
    }
}
