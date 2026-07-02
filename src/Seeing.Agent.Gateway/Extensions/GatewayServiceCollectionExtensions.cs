using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Hosting;

namespace Seeing.Agent.Gateway.Extensions;

/// <summary>
/// Gateway Server DI 注册扩展。
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Gateway Server 服务（<see cref="IGatewayServer"/> + 自动启动 HostedService）。
    /// 需先调用 <c>AddSeeingAgent</c>，并在 <c>app.Run()</c> 前执行 <c>InitializeSeeingAgentAsync</c>。
    /// </summary>
    public static IServiceCollection AddSeeingGatewayServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatewayOptions>(configuration.GetSection("SeeingAgent:Gateway"));
        services.AddSingleton<IGatewayServer, GatewayServer>();
        services.AddHostedService<GatewayHostedService>();
        return services;
    }

    /// <summary>
    /// 使用委托配置 Gateway 选项。
    /// </summary>
    public static IServiceCollection AddSeeingGatewayServer(
        this IServiceCollection services,
        Action<GatewayOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IGatewayServer, GatewayServer>();
        services.AddHostedService<GatewayHostedService>();
        return services;
    }
}
