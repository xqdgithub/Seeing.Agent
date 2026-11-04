using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.SystemOne.Extensions;

/// <summary>Seeing.Agent.Llm.SystemOne 包 DI 注册扩展。</summary>
public static class SystemOneServiceExtensions
{
    /// <summary>
    /// 注册 <see cref="SystemOneModule"/>（TypeSafe / Decision 协议客户端工厂）。
    /// 须在 <c>AddSeeingCore</c> 之前调用。
    /// <para>原 <c>SystemOne</c> 配置节登记已随平行栈删除：provider 走 <c>Providers.typesafe</c>。</para>
    /// </summary>
    public static IServiceCollection AddSystemOne(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);

        var module = new SystemOneModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
