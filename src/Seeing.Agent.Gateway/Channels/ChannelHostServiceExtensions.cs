using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// ChannelHost 管理的 DI 注册扩展。
/// </summary>
public static class ChannelHostServiceExtensions
{
    /// <summary>
    /// 注册 ChannelHost 进程管理服务（ChannelHostManager + ConfigStore + HostedService）。
    /// 需先调用 <c>AddGatewayChannelRegistry()</c> 并在 <c>app.Run()</c> 前执行 <c>ReloadGatewayChannelRegistry()</c>。
    /// </summary>
    public static IServiceCollection AddChannelHostManagement(this IServiceCollection services)
    {
        services.AddSingleton<ChannelHostConfigStore>();
        services.AddSingleton<ChannelHostManager>();
        services.AddHostedService<ChannelHostHostedService>();

        // 配置重载处理器（依赖 ChannelHostHostedService，必须在其后注册）
        services.AddSingleton<IReloadHandler, ChannelHostReloadHandler>();
        return services;
    }
}
