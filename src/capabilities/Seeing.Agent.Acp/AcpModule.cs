using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Commands;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Acp.Transport;

namespace Seeing.Agent.Acp;

/// <summary>
/// ACP 能力模块 — id=<c>acp</c>；连接管理器由 Activate/Deactivate 经 <see cref="AcpConnectionOwner"/> 自管，
/// 并收编透传 Agent、命令与配置重载 Handler 的注册/撤销。
/// </summary>
public sealed class AcpModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedTools = ["acp", "acp_status"];

    private readonly AcpModuleActivity? _activity;
    private readonly AcpConnectionOwner? _connectionOwner;
    private readonly IUiContributionRegistry? _ui;
    private readonly List<string> _registeredCommandNames = new();

    /// <summary>本模块登记过的 ACP 动态技能命令名（撤销的输入集合，含已禁用项）。</summary>
    private readonly HashSet<string> _registeredSkillCommandNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 命令注册闸：目录事件在 watcher / 执行线程并发投递，同时 Deactivate 也会改同一组集合。
    /// 与 <c>DynamicSkillCommandSync</c> 同构——集合读写必须在同一把锁内完成。
    /// </summary>
    private readonly object _commandGate = new();

    /// <summary>技能命令同步是否启用：Deactivate 后晚到的目录事件不得把命令重新登记回来。</summary>
    private bool _skillCommandSyncEnabled;

    /// <summary>目录快照订阅：技能出现 / 删除 / 被禁用都驱动 ACP 命令同步。</summary>
    private IDisposable? _skillCatalogSubscription;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public AcpModule()
    {
    }

    /// <summary>DI 解析用：持有活动门控与连接所有者。</summary>
    public AcpModule(
        AcpModuleActivity activity,
        AcpConnectionOwner connectionOwner,
        IUiContributionRegistry? uiRegistry = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _connectionOwner = connectionOwner ?? throw new ArgumentNullException(nameof(connectionOwner));
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "acp";

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
        // 实际 DI 登记由 AddSeeingAcp 完成。
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/acp", "ACP", "robot", ["acp"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 40),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null || _connectionOwner is null)
        {
            throw new InvalidOperationException(
                "AcpModule.ActivateAsync requires DI-resolved AcpModule (activity + connection owner).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connectionOwner.EnsureCreated();
        _activity.MarkActive();
        _ui?.Register(this);

        await RegisterAgentsAsync(services, cancellationToken).ConfigureAwait(false);
        AttachReloadHandler(services);
        RegisterCommands(services);

        // Hook 与本模块生命周期绑定：Activate 登记、Deactivate 撤销（不再依赖裸 HostedService）。
        RegisterHooks(services);

        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<AcpTool>() is { } acp)
            await tm.RegisterToolAsync(acp, cancellationToken).ConfigureAwait(false);
        if (services.GetService<AcpStatusTool>() is { } status)
            await tm.RegisterToolAsync(status, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // 先退订目录事件：退订后同步器不会再把新命令塞回注册表
        _skillCatalogSubscription?.Dispose();
        _skillCatalogSubscription = null;

        UnregisterHooks(services);
        UnregisterCommands(services);
        DetachReloadHandler(services);
        await UnregisterAgentsAsync(services).ConfigureAwait(false);

        var tm = services.GetService<IToolManager>();
        if (tm is not null)
        {
            foreach (var id in ProvidedTools)
                await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
        }

        _ui?.Unregister(Id);

        if (_activity is null || _connectionOwner is null)
            return;

        _activity.MarkInactiveAndWake();
        await _connectionOwner.ReleaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RegisterAgentsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<IAgentRegistry>() is not { } agentRegistry
            || services.GetService<IAcpBackendRegistry>() is not { } backendRegistry
            || services.GetService<IOptionsMonitor<AcpOptions>>() is not { } options)
        {
            return;
        }

        var logger = services.GetService<ILogger<AcpModule>>() ?? NullLogger<AcpModule>.Instance;
        await AcpDynamicAgentRegistrar.RegisterAsync(
            agentRegistry, backendRegistry, options, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UnregisterAgentsAsync(IServiceProvider services)
    {
        if (services.GetService<IAgentRegistry>() is not { } agentRegistry)
            return;

        var logger = services.GetService<ILogger<AcpModule>>() ?? NullLogger<AcpModule>.Instance;

        IReadOnlyList<AgentDefinition> agents;
        try
        {
            agents = await agentRegistry.GetAgentsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ACP 模块停用时枚举透传 Agent 失败，跳过注销");
            return;
        }

        foreach (var agent in agents)
        {
            if (agent.Runtime == AgentRuntime.AcpPassthrough
                && agent.Tags.Contains(AcpDynamicAgentRegistrar.AutoTag, StringComparer.OrdinalIgnoreCase))
            {
                agentRegistry.UnregisterAgent(agent.Name);
            }
        }
    }

    private static void AttachReloadHandler(IServiceProvider services)
    {
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<AcpReloadHandler>() is { } handler)
        {
            registry.RegisterHandler(handler);
        }
    }

    private static void DetachReloadHandler(IServiceProvider services)
    {
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<AcpReloadHandler>() is { } handler)
        {
            registry.UnregisterHandler(handler);
        }
    }

    private static void RegisterHooks(IServiceProvider services)
    {
        if (services.GetService<IHookManager>() is not { } hooks)
            return;

        if (services.GetService<AcpSessionLifecycleHook>() is { } lifecycle)
            hooks.RegisterMulti(lifecycle);
    }

    private static void UnregisterHooks(IServiceProvider services)
    {
        if (services.GetService<IHookManager>() is not { } hooks)
            return;

        if (services.GetService<AcpSessionLifecycleHook>() is { } lifecycle)
            hooks.Remove(lifecycle);
    }

    private void RegisterCommands(IServiceProvider services)
    {
        if (services.GetService<ICommandRegistry>() is not { } registry
            || services.GetService<ICommandDiscovery>() is not { } discovery)
        {
            return;
        }

        lock (_commandGate)
        {
            _registeredCommandNames.Clear();
            // 先关同步开关：下面按快照对齐完成后再打开（避免订阅与首次对齐之间插入半个状态）
            _skillCommandSyncEnabled = false;
        }

        if (services.GetService<AcpCommands>() is { } acpCommands)
        {
            foreach (var command in discovery.DiscoverFromType(acpCommands.GetType(), acpCommands))
            {
                registry.Register(command);
                lock (_commandGate)
                    _registeredCommandNames.Add(command.Metadata.Name);
            }
        }

        // 动态技能命令：订阅目录事件同步（删除 / 禁用都要撤销，修复旧实现只按启用集合求差的 bug）
        if (services.GetService<ISkillCatalog>() is { } catalog)
        {
            var logger = services.GetService<ILogger<AcpModule>>() ?? NullLogger<AcpModule>.Instance;
            lock (_commandGate)
            {
                _registeredSkillCommandNames.Clear();
                _skillCommandSyncEnabled = true;
            }

            _skillCatalogSubscription = catalog.Subscribe(snapshot => SyncSkillCommands(registry, snapshot, logger));
            SyncSkillCommands(registry, catalog.Current, logger);
        }
    }

    /// <summary>
    /// 按目录快照对齐 ACP 动态技能命令：登记启用项、撤销「本模块登记过但当前不可调用」的项。
    /// <para>整段集合读写都在 <see cref="_commandGate"/> 内：目录事件可能并发投递，且与
    /// <see cref="UnregisterCommands"/> 竞争（否则丢命令 / 集合被并发修改）。</para>
    /// </summary>
    private void SyncSkillCommands(ICommandRegistry registry, SkillCatalogSnapshot snapshot, ILogger logger)
    {
        lock (_commandGate)
        {
            // 已停用：晚到的目录事件不得重新登记命令（Deactivate 幂等）
            if (!_skillCommandSyncEnabled)
                return;

            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Entries)
            {
                if (!snapshot.DisabledNames.Contains(entry.Info.Name))
                    desired.Add(entry.Info.Name);
            }

            foreach (var name in _registeredSkillCommandNames.Where(name => !desired.Contains(name)).ToList())
            {
                _registeredSkillCommandNames.Remove(name);
                _registeredCommandNames.Remove(name);

                // 仅撤销本模块登记的 ACP 透传命令，避免误删同名默认命令
                var existing = registry.GetCommand(name);
                if (existing is AcpDynamicSkillCommand)
                    registry.Unregister(name);
            }

            foreach (var name in desired.Where(name => !_registeredSkillCommandNames.Contains(name)))
            {
                try
                {
                    var description = snapshot.Entries
                        .FirstOrDefault(entry => string.Equals(entry.Info.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?.Info.Description;

                    registry.Register(new AcpDynamicSkillCommand(name, description));
                    _registeredSkillCommandNames.Add(name);
                    _registeredCommandNames.Add(name);
                }
                catch (Exception ex)
                {
                    // 单个命令登记失败不得中断其余同步，但必须可观测（下次目录事件会重试）
                    logger.LogWarning(ex, "登记 ACP 动态技能命令失败：{SkillName}", name);
                }
            }
        }
    }

    private void UnregisterCommands(IServiceProvider services)
    {
        lock (_commandGate)
        {
            // 先关同步开关：撤销之后任何晚到事件都不得复活命令（Deactivate 幂等）
            _skillCommandSyncEnabled = false;

            if (_registeredCommandNames.Count == 0)
                return;

            if (services.GetService<ICommandRegistry>() is { } registry)
            {
                foreach (var name in _registeredCommandNames)
                {
                    var existing = registry.GetCommand(name);
                    // 仅撤销运行时限定命令（AcpPassthrough），避免误删同名默认命令（如内置 /clear）
                    if (existing is not null && existing.Metadata.SupportedRuntimes.Length > 0)
                        registry.Unregister(name);
                }
            }
            _registeredCommandNames.Clear();
            _registeredSkillCommandNames.Clear();
        }
    }
}
