using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Commands;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Scheduler.Skills;
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
    private readonly List<string> _registeredSkillNames = new();
    private readonly List<string> _registeredCommandNames = new();

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

        RegisterEmbeddedSkills(services);
        RegisterCommands(services, cancellationToken);

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

    private void RegisterEmbeddedSkills(IServiceProvider services)
    {
        if (services.GetService<ISkillManager>() is not { } skillManager)
            return;

        var logger = services.GetService<ILogger<SchedulerModule>>()
                     ?? NullLogger<SchedulerModule>.Instance;
        _registeredSkillNames.Clear();
        _registeredSkillNames.AddRange(SchedulerSkillRegistrar.Register(skillManager, logger));
    }

    private void RegisterCommands(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ICommandRegistry>() is not { } registry
            || _manager is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var commands = new ICommand[]
        {
            new CronListCommand(_manager),
            new CronRunCommand(_manager),
            new HeartbeatRunCommand(_manager)
        };

        _registeredCommandNames.Clear();
        foreach (var command in commands)
        {
            registry.Register(command);
            _registeredCommandNames.Add(command.Metadata.Name);
        }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        UnregisterCommands(services);
        UnregisterEmbeddedSkills(services);

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

    private void UnregisterCommands(IServiceProvider services)
    {
        if (_registeredCommandNames.Count == 0)
            return;

        if (services.GetService<ICommandRegistry>() is { } registry)
        {
            foreach (var name in _registeredCommandNames)
                registry.Unregister(name);
        }
        _registeredCommandNames.Clear();
    }

    private void UnregisterEmbeddedSkills(IServiceProvider services)
    {
        if (_registeredSkillNames.Count == 0)
            return;

        if (services.GetService<ISkillManager>() is { } skillManager)
            SchedulerSkillRegistrar.Unregister(skillManager, _registeredSkillNames);
        _registeredSkillNames.Clear();
    }
}
