using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Engine;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>
/// 工作区切换时停掉调度器；重载完成后按新根重建（SQLite 连接串在注册时写死）。
/// </summary>
public sealed class SchedulerWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IQuartzSchedulerLifecycle _engine;
    private readonly ILogger<SchedulerWorkspaceParticipant> _logger;

    /// <summary>创建参与者。</summary>
    public SchedulerWorkspaceParticipant(
        IQuartzSchedulerLifecycle engine,
        ILogger<SchedulerWorkspaceParticipant> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>切换前调度器是否在运行（由 <see cref="OnApplyingAsync"/> 记录）。</summary>
    public bool WasStarted { get; set; }

    /// <inheritdoc/>
    public string ComponentId => "scheduler";

    /// <inheritdoc/>
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasStarted = _engine.IsStarted;
        await _engine.StopAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("工作区切换：调度器已停止（切换前运行={WasStarted}）", WasStarted);
    }

    /// <inheritdoc/>
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        await _engine.RebuildAsync(ct).ConfigureAwait(false);

        if (WasStarted)
            await _engine.StartAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：调度器已按新工作区重建: {Root}", context.NewRoot);
    }
}
