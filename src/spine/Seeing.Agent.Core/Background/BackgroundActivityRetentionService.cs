using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台活动的保留期回收循环：周期性删除过期终态活动与已结束的 outbox 批次。
/// <para>
/// <b>为什么需要它</b>：启动收敛只处理「上一代进程留下的残骸」，而本进程跑上一周后自己产生的
/// 终态记录与批次文件没有任何东西会清理——活动根与 outbox 会单调增长。
/// </para>
/// <para>
/// <b>只删终态</b>：<c>Running</c> / <c>Pending</c> 一律保留；判定与删除的权威都在
/// <see cref="IBackgroundActivityControl.PruneAsync"/> 与 <see cref="IBackgroundActivityOutbox.PruneAsync"/>，
/// 本服务只负责定时与计算截止时刻。
/// </para>
/// <para>
/// <b>时钟</b>：等待与截止时刻都由注入的 <see cref="TimeProvider"/> 驱动，测试用
/// <c>FakeTimeProvider</c> 确定性推进，不真等 1 小时。
/// </para>
/// <para>
/// <b>失败不阻塞</b>：扫描异常只记日志并等下一个周期——回收是尽力而为的维护动作，
/// 不能因为它失败就终止循环。
/// </para>
/// </summary>
internal sealed class BackgroundActivityRetentionService : IHostedService, IAsyncDisposable
{
    private readonly IBackgroundActivityControl _control;
    private readonly IBackgroundActivityOutbox _outbox;
    private readonly IOptionsMonitor<BackgroundActivityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackgroundActivityRetentionService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BackgroundActivityRetentionService(
        IBackgroundActivityControl control,
        IBackgroundActivityOutbox outbox,
        IOptionsMonitor<BackgroundActivityOptions> options,
        TimeProvider timeProvider,
        ILogger<BackgroundActivityRetentionService> logger)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
            return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        var loop = _loop;

        _cts = null;
        _loop = null;

        if (cts is null)
            return;

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);

            if (loop is not null)
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主关停超时：循环已收到取消信号，残留记录下次启动再清。
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var options = _options.CurrentValue;

            // 间隔与保留期都按当前配置读：热重载改完下一轮就生效。
            var interval = options.CleanupInterval > TimeSpan.Zero
                ? options.CleanupInterval
                : TimeSpan.FromHours(1);

            try
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var retention = options.Retention > TimeSpan.Zero ? options.Retention : TimeSpan.Zero;
            var cutoff = _timeProvider.GetLocalNow() - retention;

            try
            {
                var activities = await _control.PruneAsync(cutoff, ct).ConfigureAwait(false);
                var batches = await _outbox.PruneAsync(cutoff, ct).ConfigureAwait(false);

                if (activities > 0 || batches > 0)
                {
                    _logger.LogInformation(
                        "后台活动保留期回收完成 Activities={Activities} Batches={Batches} Cutoff={Cutoff}",
                        activities,
                        batches,
                        cutoff);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "后台活动保留期回收失败，等待下一个清理周期");
            }
        }
    }
}
