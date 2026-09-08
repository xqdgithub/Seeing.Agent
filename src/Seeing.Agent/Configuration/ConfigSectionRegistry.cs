using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 可变的配置节注册表 — 宿主 <c>new</c> 后共享给 <see cref="UnifiedConfigManager"/> 与能力包。
/// </summary>
public sealed class ConfigSectionRegistry : IConfigSectionRegistry
{
    private readonly Dictionary<string, ConfigSectionMeta> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(ConfigSectionMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (string.IsNullOrWhiteSpace(meta.Key))
            throw new ArgumentException("配置节 Key 不能为空", nameof(meta));

        _sections[meta.Key] = meta;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ConfigSectionMeta> Sections => _sections.Values;

    /// <inheritdoc />
    public bool TryGet(string key, out ConfigSectionMeta meta)
        => _sections.TryGetValue(key, out meta!);

    /// <summary>
    /// 注册脊柱节（modules/seams/agents/permission/models/workspace + 核心残留）。
    /// 能力包节由各自 <c>ConfigureServices</c> 调用 <see cref="Register"/>。
    /// </summary>
    public void RegisterSpineSections()
    {
        Register(new("DefaultModel", "seeing.json", ConfigScope.Both, typeof(string)));
        Register(new("DefaultAgent", "seeing.json", ConfigScope.Both, typeof(string)));
        Register(new("Providers", "providers.json", ConfigScope.UserOnly,
            typeof(Dictionary<string, ProviderConfig>)));
        Register(new("AgentModels", "seeing.json", ConfigScope.Both,
            typeof(Dictionary<string, string>)));
        Register(new("Plugins", "seeing.json", ConfigScope.Both, typeof(List<PluginSpec>)));
        Register(new("PluginEnabled", "seeing.json", ConfigScope.Both,
            typeof(Dictionary<string, bool>)));
        Register(new("Permission", "seeing.json", ConfigScope.ProjectOnly, typeof(PermissionOptions)));
        Register(new("Workspace", "seeing.json", ConfigScope.ProjectOnly, typeof(WorkspaceOptions)));
        Register(new("GlobalWorkspaceRoot", "seeing.json", ConfigScope.UserOnly, typeof(string)));
        Register(new("ToolOutput", "seeing.json", ConfigScope.Both, typeof(ToolOutputOptions)));
        Register(new("TitleGeneration", "seeing.json", ConfigScope.Both, typeof(TitleGenerationOptions)));
    }

    /// <summary>创建仅含脊柱节的注册表（测试与无 DI 场景）。</summary>
    public static ConfigSectionRegistry CreateWithSpine()
    {
        var registry = new ConfigSectionRegistry();
        registry.RegisterSpineSections();
        return registry;
    }
}

/// <summary>
/// 从 <see cref="IServiceCollection"/> 获取或创建共享的 <see cref="IConfigSectionRegistry"/> 实例。
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

    /// <summary>
    /// 获取已注册的共享实例；若不存在则创建、注册脊柱节，并以 singleton instance 加入 DI。
    /// </summary>
    public static IConfigSectionRegistry GetOrCreateConfigSectionRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry existing)
            {
                return existing;
            }
        }

        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        return registry;
    }
}
