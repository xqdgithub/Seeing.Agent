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

        _registeredCommandNames.Clear();

        if (services.GetService<AcpCommands>() is { } acpCommands)
        {
            foreach (var command in discovery.DiscoverFromType(acpCommands.GetType(), acpCommands))
            {
                registry.Register(command);
                _registeredCommandNames.Add(command.Metadata.Name);
            }
        }

        if (services.GetService<ISkillManager>() is { } skillManager)
        {
            foreach (var skill in skillManager.GetAllSkillInfos().Values)
            {
                var command = new AcpDynamicSkillCommand(skill.Name, skill.Description);
                registry.Register(command);
                _registeredCommandNames.Add(command.Metadata.Name);
            }
        }
    }

    private void UnregisterCommands(IServiceProvider services)
    {
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
    }
}
