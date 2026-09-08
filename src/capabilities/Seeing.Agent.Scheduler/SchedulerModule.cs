using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Tools;

namespace Seeing.Agent.Scheduler;

/// <summary>
/// Scheduler 能力模块 — id=<c>scheduler</c>。
/// </summary>
public sealed class SchedulerModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "cron_list",
        "cron_create",
        "cron_delete",
        "cron_disable",
        "cron_resume",
        "cron_run",
    ];

    private readonly SchedulerModuleActivity? _activity;
    private readonly IScheduleManager? _manager;
    private readonly IUiContributionRegistry? _ui;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public SchedulerModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public SchedulerModule(
        SchedulerModuleActivity activity,
        IScheduleManager manager,
        IUiContributionRegistry? uiRegistry = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "scheduler";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 实际 DI 登记由 AddSeeingScheduler 完成。
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/cron-jobs", "定时任务", "clock-circle", ["scheduler"],
            Group: NavGroups.Control, GroupIcon: NavGroups.ControlIcon, Order: 20),
        new NavContribution("/heartbeat", "心跳", "heart", ["scheduler"],
            Group: NavGroups.Control, GroupIcon: NavGroups.ControlIcon, Order: 30),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null)
        {
            throw new InvalidOperationException(
                "SchedulerModule.ActivateAsync requires DI-resolved SchedulerModule.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _activity.MarkActive();
        _ui?.Register(this);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<CronListTool>() is { } list)
            await tm.RegisterToolAsync(list, cancellationToken).ConfigureAwait(false);
        if (services.GetService<CronCreateTool>() is { } create)
            await tm.RegisterToolAsync(create, cancellationToken).ConfigureAwait(false);
        if (services.GetService<CronDeleteTool>() is { } delete)
            await tm.RegisterToolAsync(delete, cancellationToken).ConfigureAwait(false);
        if (services.GetService<CronDisableTool>() is { } disable)
            await tm.RegisterToolAsync(disable, cancellationToken).ConfigureAwait(false);
        if (services.GetService<CronResumeTool>() is { } resume)
            await tm.RegisterToolAsync(resume, cancellationToken).ConfigureAwait(false);
        if (services.GetService<CronRunTool>() is { } run)
            await tm.RegisterToolAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        _ui?.Unregister(Id);

        if (_activity is null)
            return;

        _activity.MarkInactiveAndWake();
        if (_manager is not null)
            await _manager.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
