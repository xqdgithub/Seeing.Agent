using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Background;

/// <summary>
/// 复盘后台工作者：排空队列 + 周期性触发空闲扫描。
/// <para>
/// 单循环模型：每轮先把队列中的请求全部执行完（非阻塞 <c>TryDequeue</c>），
/// 再触发一次空闲扫描，最后等待 1 分钟进入下一轮。
/// 相比「队列等待 + 定时器」双任务，单循环不会出现两条执行路径并发跑复盘。
/// </para>
/// <para>
/// 空闲扫描的策略在 <see cref="ReflectionIdleScanner"/>：那条策略里的每道门禁都决定
/// 「会不会对不该复盘的会话做复盘」，必须有独立可测的落点——
/// 埋在私有方法里就只能靠人读代码保证，而这类缺陷不报错、只静静烧预算。
/// </para>
/// </summary>
public sealed class ReflectionWorker : BackgroundService, IModuleHostedService
{
    /// <summary>模块 id（与 <see cref="ReflectionModule.Id"/> 对齐）。</summary>
    public const string ModuleId = "reflection";

    private static readonly TimeSpan s_tickInterval = TimeSpan.FromMinutes(1);

    private readonly IReflectionQueue _queue;
    private readonly IReflectionExecutor _executor;
    private readonly ReflectionIdleScanner _idleScanner;
    private readonly ReflectionModuleActivity _moduleActivity;
    private readonly ILogger<ReflectionWorker> _logger;
    private readonly ModuleHostedRunGate _run = new();

    public ReflectionWorker(
        IReflectionQueue queue,
        IReflectionExecutor executor,
        ReflectionIdleScanner idleScanner,
        ReflectionModuleActivity moduleActivity,
        ILogger<ReflectionWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _idleScanner = idleScanner ?? throw new ArgumentNullException(nameof(idleScanner));
        _moduleActivity = moduleActivity ?? throw new ArgumentNullException(nameof(moduleActivity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc cref="IModuleHostedService.ModuleId" />
    string IModuleHostedService.ModuleId => ModuleId;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning || !_moduleActivity.IsActive)
            return Task.CompletedTask;

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

    /// <inheritdoc />
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

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("复盘后台工作者已启动");

            // 时钟漂移容忍：PeriodicTimer 允许单次 tick 超时，不需要额外补 tick 逻辑。
            using var ticker = new PeriodicTimer(s_tickInterval);

            while (!stoppingToken.IsCancellationRequested && _moduleActivity.IsActive)
            {
                try
                {
                    await DrainQueueAsync(stoppingToken).ConfigureAwait(false);
                    RunIdleScan();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮失败不得终止长循环：后台复盘是尽力而为的旁路能力。
                    _logger.LogWarning(ex, "复盘后台工作者本轮处理失败，将在下一轮重试");
                }

                try
                {
                    if (!await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                        break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _run.End();
        }
    }

    /// <summary>非阻塞排空队列，逐个交给执行器。</summary>
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _moduleActivity.IsActive && _queue.TryDequeue(out var request))
        {
            try
            {
                var result = await _executor.RunAsync(request, ct).ConfigureAwait(false);
                if (!result.Executed)
                {
                    // 跳过属于正常业务结果（关闭/超预算/超频率/无来源/超时等），不能中断本轮排空。
                    _logger.LogDebug("复盘跳过：{Reason}", result.SkipReason);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "单次复盘执行失败：SessionId={SessionId}, Kind={Kind}",
                    request.SessionId,
                    request.Kind);
            }
        }
    }

    /// <summary>触发一次空闲扫描（门禁与冷却在扫描器内部）。</summary>
    private void RunIdleScan()
    {
        if (!_moduleActivity.IsActive)
            return;

        _idleScanner.Scan(DateTimeOffset.UtcNow);
    }
}
