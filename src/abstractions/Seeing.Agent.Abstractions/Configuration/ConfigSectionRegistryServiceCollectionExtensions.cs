using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 从 <see cref="IServiceCollection"/> 确保共享的 <see cref="IConfigSectionRegistry"/> 已登记。
/// </summary>
public static class ConfigSectionRegistryServiceCollectionExtensions
{
    /// <summary>
    /// 确保宿主传入的 <paramref name="registry"/> 已以 singleton instance 登记到 DI；
    /// 若已登记不同实例则抛错。
    /// </summary>
    public static void EnsureConfigSectionRegistry(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry existing)
            {
                if (!ReferenceEquals(existing, registry))
                {
                    throw new InvalidOperationException(
                        "IConfigSectionRegistry 已登记为不同实例；宿主须共享同一 registry。");
                }

                return;
            }
        }

        services.AddSingleton(registry);
    }
}
