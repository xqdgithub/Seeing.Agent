using Microsoft.Extensions.Configuration;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Gateway.Hosting;
using Seeing.Agent.Gateway.Permission;
using Seeing.Agent.Gateway.Scheduling;

namespace Seeing.Agent.Gateway.Extensions;

/// <summary>
/// Gateway Server DI 注册扩展。
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Gateway Server 服务（<see cref="IGatewayServer"/> + 自动启动 HostedService）。
    /// 须在 <c>AddSeeingCore</c> 之前调用，并在 <c>app.Run()</c> 前执行 <c>InitializeSeeingAsync</c>。
    /// Gateway 配置来自 <c>.seeing/seeing.json</c>。
    /// </summary>
    public static IServiceCollection AddSeeingGatewayServer(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(new ConfigSectionMeta(
            GatewayOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayOptions)));
        registry.Register(new ConfigSectionMeta(
            GatewayClientsOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayClientsOptions)));

        services.AddOptions<GatewayOptions>();
        services.AddSingleton<IOptions<GatewayOptions>, GatewayOptionsMonitor>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();

        services.AddSingleton<GatewayConnectionManager>();
        services.AddSingleton<GatewayScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => sp.GetRequiredService<GatewayScheduleDispatcher>());
        services.AddSingleton<IGatewayServer, GatewayServer>();
        services.AddHostedService<GatewayHostedService>();
        services.AddSingleton<ISeeingModule>(sp =>
            new GatewayModule(sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));
        services.AddGatewayPermissionChannel();
        return services;
    }

    /// <summary>
    /// 注册 Gateway Server 服务（兼容重载，配置仍来自 .seeing/seeing.json）。
    /// </summary>
    public static IServiceCollection AddSeeingGatewayServer(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        IConfiguration configuration)
    {
        _ = configuration;
        return AddSeeingGatewayServer(services, registry);
    }

    /// <summary>
    /// 使用委托配置 Gateway 选项。须在 <c>AddSeeingCore</c> 之前调用。
    /// </summary>
    public static IServiceCollection AddSeeingGatewayServer(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        Action<GatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(new ConfigSectionMeta(
            GatewayOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayOptions)));
        registry.Register(new ConfigSectionMeta(
            GatewayClientsOptions.SectionName, "seeing.json", ConfigScope.ProjectOnly, typeof(GatewayClientsOptions)));

        services.Configure(configure);
        services.AddSingleton<GatewayConnectionManager>();
        services.AddSingleton<GatewayScheduleDispatcher>();
        services.AddSingleton<IScheduledJobDispatcher>(sp => sp.GetRequiredService<GatewayScheduleDispatcher>());
        services.AddSingleton<IGatewayServer, GatewayServer>();
        services.AddHostedService<GatewayHostedService>();
        services.AddSingleton<ISeeingModule>(sp =>
            new GatewayModule(sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));
        services.AddGatewayPermissionChannel();
        return services;
    }

    /// <summary>
    /// 注册 Gateway 宿主通道：具体类型 + 接口映射（同一实例）。
    /// 必须显式映射到 <see cref="IPermissionChannel"/>，否则具体类型不进
    /// <c>IEnumerable&lt;IPermissionChannel&gt;</c>，TryAutoApprove 会被 Core 兜底 DenyAll 遮蔽。
    /// </summary>
    private static IServiceCollection AddGatewayPermissionChannel(this IServiceCollection services)
    {
        services.AddSingleton<GatewayPermissionChannel>();
        services.AddSingleton<IPermissionChannel>(sp => sp.GetRequiredService<GatewayPermissionChannel>());
        return services;
    }
}
