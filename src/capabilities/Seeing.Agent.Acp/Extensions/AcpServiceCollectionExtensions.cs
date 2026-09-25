using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
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
        // 以具体类型登记，不登记为 IReloadHandler：避免宿主启动期全量挂载，
        // 改由 AcpModule Activate/Deactivate 动态挂接/撤销（停用后热重载不得复活透传 Agent）。
        services.AddSingleton<AcpReloadHandler>();
        services.AddSingleton<AcpPermissionBridge>();
        services.AddSingleton<AcpFileSystemBridge>();
        services.AddSingleton<AcpTerminalBridge>();
        services.AddSingleton<SeeingAcpClientFactory>();
        services.AddSingleton<AcpModuleActivity>();
        services.AddSingleton<Func<AcpConnectionManager>>(sp =>
            () => ActivatorUtilities.CreateInstance<AcpConnectionManager>(sp));
        // 空壳 Owner：Activate 才 EnsureCreated，Deactivate Release；不在此永久 new manager。
        services.AddSingleton<AcpConnectionOwner>(sp =>
            new AcpConnectionOwner(sp.GetRequiredService<Func<AcpConnectionManager>>()));
        // 解析时取当前 Owner 持有实例（须已 Activate）；禁止 AddSingleton 工厂永久占有。
        services.AddTransient<AcpConnectionManager>(sp =>
            sp.GetRequiredService<AcpConnectionOwner>().Manager);
        services.AddSingleton<ISeeingModule>(sp => new AcpModule(
            sp.GetRequiredService<AcpModuleActivity>(),
            sp.GetRequiredService<AcpConnectionOwner>(),
            sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));
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

        // 注册 ACP 专属命令
        services.AddSingleton<AcpCommands>();

        services.AddModuleHostedService<AcpConnectionIdleCleanupHostedService>();

        // 追加 ACP 运行时实现；门面仍为 AgentExecutorRouter
        services.AddSingleton<IAgentExecutorImplementation, AcpAgentExecutor>();
        return services;
    }

    /// <summary>
    /// 已废弃的 ACP 命令注册入口。命令注册已收编进 <see cref="AcpModule.ActivateAsync"/>，
    /// 保留空实现仅用于宿主兼容；停用模块时命令由模块 Deactivate 对称撤销。
    /// </summary>
    public static IServiceProvider InitializeAcpCommands(this IServiceProvider services)
    {
        return services;
    }
}
