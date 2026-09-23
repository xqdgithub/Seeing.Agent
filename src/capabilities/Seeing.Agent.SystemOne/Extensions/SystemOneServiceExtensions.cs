using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Extensions;

/// <summary>SystemOne 包 DI 注册扩展。</summary>
public static class SystemOneServiceExtensions
{
    /// <summary>
    /// 注册 Seeing.Agent.SystemOne 全部服务并登记 <c>SystemOne</c> 配置节。
    /// 须在 <c>AddSeeingCore</c> 之前调用。
    /// </summary>
    public static IServiceCollection AddSystemOne(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);
        registry.Register(SystemOneConfigStore.SectionMeta);

        var module = new SystemOneModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
