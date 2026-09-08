using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Seeing.Agent.Abstractions.Modules;

/// <summary>将同一实例登记为 <see cref="IHostedService"/> 与 <see cref="IModuleHostedService"/>。</summary>
public static class ModuleHostedServiceServiceCollectionExtensions
{
    /// <summary>
    /// 登记模块长驻服务：Generic Host 启动 + 模块 Activate 复活共用同一实例。
    /// </summary>
    public static IServiceCollection AddModuleHostedService<TService>(this IServiceCollection services)
        where TService : class, IHostedService, IModuleHostedService
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TService>());
        services.AddSingleton<IModuleHostedService>(sp => sp.GetRequiredService<TService>());
        return services;
    }
}
