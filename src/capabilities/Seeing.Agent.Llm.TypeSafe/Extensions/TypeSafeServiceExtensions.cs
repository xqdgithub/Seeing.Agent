using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.TypeSafe.Extensions;

/// <summary>Seeing.Agent.Llm.TypeSafe 包 DI 注册扩展。</summary>
public static class TypeSafeServiceExtensions
{
    /// <summary>
    /// 注册 <see cref="TypeSafeModule"/>（TypeSafe / Decision 协议客户端工厂）。
    /// 须在 <c>AddSeeingCore</c> 之前调用。
    /// <para>Provider 不在这里：内置 Provider 已下沉为插件 <c>provider.typesafe-api</c>，
    /// 用户也可在 <c>Providers</c> 节自建任意 id 的 <c>typesafe</c> 协议 Provider。</para>
    /// </summary>
    public static IServiceCollection AddTypeSafe(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);

        var module = new TypeSafeModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
