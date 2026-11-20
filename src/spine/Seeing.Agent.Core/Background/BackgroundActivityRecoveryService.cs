using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 宿主启动时的后台活动收敛（spec §16）：先收敛孤儿活动，再清掉上一代实例遗留的待投递批次。
/// <para>
/// <b>为什么在启动时做</b>：上一代进程崩溃后，盘上会留下 <c>Running</c> 记录与本实例无关的
/// <c>Pending</c> 通知；没有任何在跑的东西会去收拾它们。启动是唯一一次「确定能看到全部残骸」的时机。
/// </para>
/// <para>
/// <b>失败不阻断启动</b>：收敛是尽力而为的清理——清理失败最多让残骸留到下次启动，
/// 而抛出异常会让整个宿主起不来。两类失败都只记日志。
/// </para>
/// <para>
/// <b>顺序</b>：先孤儿（把死实例的 <c>Running</c> 落成 <c>Interrupted</c>），再 outbox
/// （把死实例的 <c>Pending</c> 标 <c>Dropped</c>）。反过来也能工作，但先收敛记录让日志的因果更清楚。
/// </para>
/// </summary>
internal sealed class BackgroundActivityRecoveryService : IHostedService
{
    private readonly IBackgroundActivityControl _control;
    private readonly IBackgroundActivityOutbox _outbox;
    private readonly ILogger<BackgroundActivityRecoveryService> _logger;

    public BackgroundActivityRecoveryService(
        IBackgroundActivityControl control,
        IBackgroundActivityOutbox outbox,
        ILogger<BackgroundActivityRecoveryService> logger)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reconciled = await _control
                .ReconcileOrphanedActivitiesAsync(cancellationToken)
                .ConfigureAwait(false);

            if (reconciled > 0)
            {
                _logger.LogInformation(
                    "启动收敛：孤儿后台活动已标记为 Interrupted Count={Count}", reconciled);
            }
        }
        catch (Exception ex)
        {
            // 含取消：收敛是启动期清理，任何异常都不允许把宿主启动打断。
            _logger.LogWarning(ex, "启动收敛：孤儿后台活动收敛失败（不阻断启动）");
        }

        try
        {
            var dropped = await _outbox
                .ReconcileDeadHostsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (dropped > 0)
            {
                _logger.LogInformation(
                    "启动收敛：死宿主待投递通知已标记为 Dropped Count={Count}", dropped);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动收敛：死宿主待投递通知清理失败（不阻断启动）");
        }
    }

    /// <inheritdoc />
    /// <remarks>无在途状态需要收尾：收敛是一次性扫描，结果已落盘。</remarks>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
