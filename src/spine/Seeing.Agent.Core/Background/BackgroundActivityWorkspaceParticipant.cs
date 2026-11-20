using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 工作区切换参与者：切根之前停掉跑在旧根上的后台活动，切根之后收敛新根里的孤儿记录。
/// <para>
/// <b>为什么必须在改根之前取消</b>：活动输出与快照路径都由工作区推导
/// （<c>{项目根}/.seeing/background-activities</c>）。改根之后再取消，终态会写进新根，
/// 旧根会永远留着一条 <c>Running</c> 记录；而输出已经写进旧根，两边对不上。
/// </para>
/// <para>
/// <b>已知取舍</b>：本参与者按「切换一定会成功」执行。若后续重载失败并回滚到旧根，
/// 被取消的活动不会复活（spec §9.5「工作区切换」）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IBackgroundActivityControl _control;
    private readonly IBackgroundActivityNotificationQueue _outbox;
    private readonly ILogger<BackgroundActivityWorkspaceParticipant> _logger;

    public BackgroundActivityWorkspaceParticipant(
        IBackgroundActivityControl control,
        IBackgroundActivityNotificationQueue outbox,
        ILogger<BackgroundActivityWorkspaceParticipant> logger)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ComponentId => "background-activities";

    /// <inheritdoc />
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        var cancelled = await _control
            .CancelAllAsync(BackgroundActivityCancelReason.WorkspaceSwitched, ct)
            .ConfigureAwait(false);

        if (cancelled > 0)
        {
            _logger.LogInformation(
                "工作区切换：已取消后台活动 Count={Count} OldRoot={OldRoot}",
                cancelled,
                context.OldRoot);
        }

        // 旧根里本实例的待投递批次随切根失去意义：显式传旧根标记 dropped。
        // 绝不能在这里读工作区提供者——回滚/竞态下它可能已指向新根。
        try
        {
            var dropped = await _outbox
                .MarkDroppedAsync(
                    BackgroundActivityHostIdentity.Current,
                    context.OldRoot,
                    reason: "workspace_switched")
                .ConfigureAwait(false);

            if (dropped > 0)
            {
                _logger.LogInformation(
                    "工作区切换：旧根待投递通知已丢弃 Count={Count} OldRoot={OldRoot}",
                    dropped,
                    context.OldRoot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 丢弃失败不影响切换：批次留在旧根，最多是「切回旧工作区时补投一次」。
            _logger.LogWarning(ex, "工作区切换：旧根待投递通知清理失败 OldRoot={OldRoot}", context.OldRoot);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 切换完成后扫描一次新根（spec §16 的第二个触发点）：旧根里别的实例留下的
    /// <c>Running</c> 已随切根离开视野，不会再被任何人收敛。
    /// 失败只记日志，不视为切换失败。
    /// </remarks>
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        try
        {
            var reconciled = await _control.ReconcileOrphanedActivitiesAsync(ct).ConfigureAwait(false);
            if (reconciled > 0)
            {
                _logger.LogInformation(
                    "工作区切换：新根孤儿活动已收敛 Count={Count} Root={Root}",
                    reconciled,
                    context.NewRoot);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作区切换：新根孤儿活动收敛失败 Root={Root}", context.NewRoot);
        }
    }
}
