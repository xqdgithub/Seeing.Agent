using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Commands;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Configuration;
using Seeing.Agent.Skills;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// ACP 包 DI 注册扩展。
/// </summary>
public static class AcpServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Seeing.Agent.Acp 全部服务，并追加 ACP <see cref="IAgentExecutorImplementation"/>。
    /// 须在 <c>AddSeeingCore</c> 之前调用（不替换 <see cref="IAgentExecutor"/> 门面）。
    /// </summary>
    public static IServiceCollection AddSeeingAcp(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(
            new ConfigSectionMeta(AcpOptions.SectionName, "seeing.json", ConfigScope.UserOnly, typeof(AcpOptions)));

        services.AddOptions<AcpOptions>();
        services.AddSingleton<AcpOptionsMonitor>();
        services.AddSingleton<IOptions<AcpOptions>>(sp => sp.GetRequiredService<AcpOptionsMonitor>());
        services.AddSingleton<IOptionsMonitor<AcpOptions>>(sp => sp.GetRequiredService<AcpOptionsMonitor>());

        services.AddSingleton<IAcpBackendRegistry, AcpBackendRegistry>();
        services.AddSingleton<IAcpConfigurationReloader, AcpConfigurationReloader>();
        services.AddSingleton<IReloadHandler, AcpReloadHandler>();
        services.AddSingleton<AcpPermissionBridge>();
        services.AddSingleton<AcpFileSystemBridge>();
        services.AddSingleton<AcpTerminalBridge>();
        services.AddSingleton<SeeingAcpClientFactory>();
        services.AddSingleton<AcpModuleActivity>();
        services.AddSingleton<Func<AcpConnectionManager>>(sp =>
            () => ActivatorUtilities.CreateInstance<AcpConnectionManager>(sp));
        // 进程内仍共享一个 manager 实例供消费者注入；工厂供 Module Activate 自管新建。
        services.AddSingleton<AcpConnectionManager>(sp =>
            sp.GetRequiredService<Func<AcpConnectionManager>>()());
        services.AddSingleton<ISeeingModule>(sp => new AcpModule(
            sp.GetRequiredService<AcpModuleActivity>(),
            sp.GetRequiredService<Func<AcpConnectionManager>>(),
            sp.GetRequiredService<AcpConnectionManager>()));
        services.AddSingleton<AcpLifecycleService>();
        services.AddSingleton<AcpSessionStore>();
        services.AddSingleton<AcpTaskStore>();
        services.AddSingleton<AcpSessionConfigApplier>();
        services.AddSingleton<AcpEventMapper>();
        services.AddSingleton<ContentBlockMapper>();
        services.AddSingleton<AcpMcpServerMapper>();
        services.AddSingleton<AcpCancellationCoordinator>();
        services.AddSingleton<AcpSessionLifecycleHook>();
        services.AddSingleton<IAcpSessionRunner, AcpSessionRunner>();
        services.AddSingleton<AcpPassthroughExecutor>();
        services.AddSingleton<AcpTool>();
        services.AddSingleton<AcpStatusTool>();
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<AcpTool>());
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<AcpStatusTool>());

        // 注册 ACP 专属命令
        services.AddSingleton<AcpCommands>();

        services.AddHostedService<AcpHookRegistrationHostedService>();
        services.AddHostedService<AcpAgentRegistrationHostedService>();
        services.AddHostedService<AcpConnectionIdleCleanupHostedService>();

        // 追加 ACP 运行时实现；门面仍为 AgentExecutorRouter
        services.AddSingleton<IAgentExecutorImplementation, AcpAgentExecutor>();
        return services;
    }

    /// <summary>
    /// 初始化 ACP 命令注册（在服务提供者构建后调用）
    /// </summary>
    public static IServiceProvider InitializeAcpCommands(this IServiceProvider services)
    {
        var registry = services.GetRequiredService<ICommandRegistry>();
        var discovery = services.GetRequiredService<CommandDiscovery>();

        // 发现 ACP 命令
        var acpCommands = services.GetService<AcpCommands>();
        if (acpCommands != null)
        {
            var commands = discovery.DiscoverFromType(acpCommands.GetType(), acpCommands);
            registry.RegisterAll(commands);
        }

        // 动态注册 ACP skill 透传命令
        var skillManager = services.GetService<SkillManager>();
        if (skillManager != null)
        {
            foreach (var skillInfo in skillManager.GetAllSkillInfos().Values)
            {
                registry.Register(new AcpDynamicSkillCommand(skillInfo.Name, skillInfo.Description));
            }
        }

        return services;
    }
}
