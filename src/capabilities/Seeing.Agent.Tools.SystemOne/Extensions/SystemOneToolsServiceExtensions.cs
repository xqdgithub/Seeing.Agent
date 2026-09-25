using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Tools.SystemOne.Extensions;

/// <summary>SystemOne 工具包 DI 注册扩展。</summary>
public static class SystemOneToolsServiceExtensions
{
    /// <summary>
    /// 注册 Seeing.Agent.Tools.SystemOne 全部服务与模块。须在 <c>AddSeeingCore</c> 之前调用。
    /// </summary>
    public static IServiceCollection AddSystemOneTools(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);

        var module = new SystemOneToolsModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
