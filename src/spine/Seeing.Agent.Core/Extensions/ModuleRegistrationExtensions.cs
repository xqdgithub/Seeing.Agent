using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Extensions;

/// <summary>
/// 模块登记扩展 — 支持 <see cref="ReplaceModule{TModule}"/>；同 Id 未 Replace 时拒绝。
/// 两参 <c>AddSeeingModule</c> 仍由 <see cref="ServiceCollectionExtensions"/> 提供（不拒重）；
/// 带 <c>replace</c> 的重载与 <see cref="ReplaceModule{TModule}"/> 走本类型。
/// </summary>
public static class ModuleRegistrationExtensions
{
    /// <summary>
    /// 登记能力模块；若同 Id 已登记且 <paramref name="replace"/> 为 false 则抛错。
    /// </summary>
    public static IServiceCollection AddSeeingModule<TModule>(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        bool replace)
        where TModule : class, ISeeingModule, new()
    {
        ArgumentNullException.ThrowIfNull(registry);
        return RegisterCore<TModule>(services, registry, replace);
    }

    /// <summary>
    /// 替换已登记的同 Id 模块（先卸旧描述符再登记新模块）。
    /// </summary>
    public static IServiceCollection ReplaceModule<TModule>(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
        where TModule : class, ISeeingModule, new()
        => AddSeeingModule<TModule>(services, registry, replace: true);

    private static IServiceCollection RegisterCore<TModule>(
        IServiceCollection services,
        IConfigSectionRegistry registry,
        bool replace)
        where TModule : class, ISeeingModule, new()
    {
        services.EnsureConfigSectionRegistry(registry);

        var module = new TModule();
        if (string.IsNullOrWhiteSpace(module.Id))
            throw new ArgumentException($"模块 {typeof(TModule).Name} 的 Id 不能为空");

        var map = GetOrCreateMap(services);
        if (map.TryGetValue(module.Id, out var prior))
        {
            if (!replace)
            {
                throw new InvalidOperationException(
                    $"重复登记模块 id '{module.Id}'；请改用 ReplaceModule 或 AddSeeingModule(..., replace: true)。");
            }

            foreach (var descriptor in prior)
                services.Remove(descriptor);
            map.Remove(module.Id);
        }

        module.ConfigureServices(services);

        var tracked = ServiceDescriptor.Singleton<ISeeingModule>(sp =>
        {
            var agentStoreCtor = typeof(TModule).GetConstructor([typeof(IAgentStore)]);
            if (agentStoreCtor is not null)
            {
                var store = sp.GetService<IAgentStore>();
                if (store is not null)
                    return (TModule)agentStoreCtor.Invoke([store])!;
            }

            var uiCtor = typeof(TModule).GetConstructor([typeof(IUiContributionRegistry)]);
            if (uiCtor is not null)
            {
                var ui = sp.GetService<IUiContributionRegistry>();
                return (TModule)uiCtor.Invoke([ui])!;
            }

            return new TModule();
        });
        services.Add(tracked);
        map[module.Id] = [tracked];
        return services;
    }

    private static Dictionary<string, List<ServiceDescriptor>> GetOrCreateMap(IServiceCollection services)
    {
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(ModuleIdRegistrationMap) &&
                d.ImplementationInstance is ModuleIdRegistrationMap existing)
                return existing.Entries;
        }

        var map = new ModuleIdRegistrationMap();
        services.Insert(0, ServiceDescriptor.Singleton(map));
        return map.Entries;
    }

    /// <summary>DI 侧车：记录已登记模块 Id → 描述符，供 Replace / 拒重。</summary>
    private sealed class ModuleIdRegistrationMap
    {
        public Dictionary<string, List<ServiceDescriptor>> Entries { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
