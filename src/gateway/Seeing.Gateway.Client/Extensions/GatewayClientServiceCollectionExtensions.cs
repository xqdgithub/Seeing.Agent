using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Seeing.Gateway.Client.Extensions;

/// <summary>
/// Gateway Client DI 注册扩展
/// </summary>
public static class GatewayClientServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IGatewayClient"/>（按 Transport 选择 HTTP 或 WebSocket）
    /// </summary>
    public static IServiceCollection AddSeeingGatewayClient(
        this IServiceCollection services,
        Action<GatewayClientOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<HttpGatewayClient>();

        services.AddSingleton<WebSocketGatewayClient>();
        services.AddSingleton<IGatewayClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayClientOptions>>().Value;
            return options.Transport == GatewayClientTransport.WebSocket
                ? new WebSocketGatewayClientFacade(sp.GetRequiredService<WebSocketGatewayClient>())
                : sp.GetRequiredService<HttpGatewayClient>();
        });

        return services;
    }

    /// <summary>
    /// 从配置节 Gateway 注册 <see cref="IGatewayClient"/>
    /// </summary>
    public static IServiceCollection AddSeeingGatewayClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatewayClientOptions>(configuration.GetSection(GatewayClientOptions.SectionName));
        services.ConfigureGatewayClientCommon(configuration);
        return AddSeeingGatewayClient(services);
    }

    /// <summary>
    /// 绑定 Channel 配置文件根节的 Agent / Model（<see cref="GatewayClientCommonOptions"/>）。
    /// </summary>
    public static IServiceCollection ConfigureGatewayClientCommon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatewayClientCommonOptions>(options =>
        {
            options.Agent = configuration[nameof(GatewayClientCommonOptions.Agent)];
            options.Model = configuration[nameof(GatewayClientCommonOptions.Model)];
            options.Mode = configuration[nameof(GatewayClientCommonOptions.Mode)];
        });
        return services;
    }

    /// <summary>
    /// 进程内 <see cref="ISessionClient"/>（依赖根容器 <see cref="Seeing.Agent.Abstractions.Runtime.ISessionRuntime"/>）。
    /// </summary>
    public static IServiceCollection AddInProcessSessionClient(this IServiceCollection services)
    {
        services.TryAddSingleton<GatewayRunTracker>();
        services.AddSingleton<ISessionClient, InProcessSessionClient>();
        return services;
    }

    /// <summary>
    /// 远程 <see cref="ISessionClient"/>，包装同一 <see cref="WebSocketGatewayClient"/> 实例。
    /// </summary>
    public static IServiceCollection AddRemoteSessionClient(
        this IServiceCollection services,
        GatewayClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.TryAddSingleton<WebSocketGatewayClient>();
        services.TryAddSingleton<IGatewayChannelHostClient>(sp => sp.GetRequiredService<WebSocketGatewayClient>());
        services.AddSingleton<ISessionClient, RemoteSessionClient>();
        return services;
    }
}
