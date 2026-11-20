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
    private readonly ILogger<BackgroundActivityWorkspaceParticipant> _logger;

    public BackgroundActivityWorkspaceParticipant(
        IBackgroundActivityControl control,
        ILogger<BackgroundActivityWorkspaceParticipant> logger)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
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

        // TODO(Task 11)：outbox 落地后，把旧根 outbox 中本实例的 pending 批次标 dropped
        // （LastError="workspace_switched"）。旧根路径取 context.OldRoot，绝不能读切换后的工作区提供者。
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
