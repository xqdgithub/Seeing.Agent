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
public sealed class SchedulerModule : ISeeingModule, IUiContributor
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "cron_list",
        "cron_create",
        "cron_delete",
        "cron_disable",
        "cron_resume",
        "cron_run",
        "heartbeat_get",
        "heartbeat_update",
        "heartbeat_run",
    ];

    private readonly SchedulerModuleActivity? _activity;
    private readonly IScheduleManager? _manager;
    private readonly List<string> _registeredSkillNames = new();
    private readonly List<string> _registeredCommandNames = new();

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public SchedulerModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public SchedulerModule(
        SchedulerModuleActivity activity,
        IScheduleManager manager)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <inheritdoc />
    public string Id => "scheduler";

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
    /// <remarks>
    /// 三条路由：列表页与心跳进「控制中心」；任务详情是已知子路径（<c>Nav = null</c>），
    /// 迁移前由 <c>ModulePageRouteBinder</c> 补全，现由模块自己声明。
    /// </remarks>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.cron",
            Route = "/cron-jobs",
            Title = "定时任务",
            ViewKey = "page.cron",
            Requires = [Id],
            Order = 20,
            Nav = new NavPlacement
            {
                Icon = "clock-circle",
                Group = NavGroups.Control,
                GroupIcon = NavGroups.ControlIcon,
                GroupOrder = 10,
            },
        },
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.heartbeat",
            Route = "/heartbeat",
            Title = "心跳",
            ViewKey = "page.heartbeat",
            Requires = [Id],
            Order = 30,
            Nav = new NavPlacement
            {
                Icon = "heart",
                Group = NavGroups.Control,
                GroupIcon = NavGroups.ControlIcon,
                GroupOrder = 10,
            },
        },
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.cron.detail",
            Route = "/cron-jobs/{JobId}",
            Title = "任务详情",
            ViewKey = "page.cron.detail",
            Requires = [Id],
            Order = 21,
        },
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

        await RegisterEmbeddedSkillsAsync(services, cancellationToken).ConfigureAwait(false);
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

        // 心跳工具单独挂载：它们只依赖 IHeartbeatService，与 cron 工具互不进入对方的配置面。
        if (services.GetService<HeartbeatGetTool>() is { } hbGet)
            await tm.RegisterToolAsync(hbGet, cancellationToken).ConfigureAwait(false);
        if (services.GetService<HeartbeatUpdateTool>() is { } hbUpdate)
            await tm.RegisterToolAsync(hbUpdate, cancellationToken).ConfigureAwait(false);
        if (services.GetService<HeartbeatRunTool>() is { } hbRun)
            await tm.RegisterToolAsync(hbRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task RegisterEmbeddedSkillsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ISkillCatalog>() is not { } catalog)
            return;

        var logger = services.GetService<ILogger<SchedulerModule>>()
                     ?? NullLogger<SchedulerModule>.Instance;
        _registeredSkillNames.Clear();
        _registeredSkillNames.AddRange(
            await SchedulerSkillRegistrar.RegisterAsync(catalog, logger, cancellationToken)
                .ConfigureAwait(false));
    }

    private void RegisterCommands(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<ICommandRegistry>() is not { } registry
            || _manager is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 心跳命令依赖 IHeartbeatService；未注册时跳过该命令，而不是让整个模块激活失败。
        var heartbeat = services.GetService<IHeartbeatService>();

        var commands = new List<ICommand>
        {
            new CronListCommand(_manager),
            new CronRunCommand(_manager)
        };

        if (heartbeat is not null)
            commands.Add(new HeartbeatRunCommand(heartbeat));

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
        await UnregisterEmbeddedSkillsAsync(services, cancellationToken).ConfigureAwait(false);

        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

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

    private async Task UnregisterEmbeddedSkillsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (_registeredSkillNames.Count == 0)
            return;

        if (services.GetService<ISkillCatalog>() is { } catalog)
        {
            var logger = services.GetService<ILogger<SchedulerModule>>()
                         ?? NullLogger<SchedulerModule>.Instance;
            await SchedulerSkillRegistrar
                .UnregisterAsync(catalog, _registeredSkillNames, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        _registeredSkillNames.Clear();
    }
}
