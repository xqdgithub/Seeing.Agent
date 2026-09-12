using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.ModelCapabilities;

/// <summary>
/// 模型能力编排模块 — id=<c>llm.modelcapabilities</c>。
/// </summary>
public sealed class ModelCapabilitiesModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "llm.modelcapabilities";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        TryRegisterConfigSection(services);

        services.TryAddSingleton(sp =>
            new ConfigSectionOptionsMonitor<ModelCapabilitiesOptions>(
                sp.GetRequiredService<IConfigSectionStore>(),
                ModelCapabilitiesOptions.SectionName));
        services.TryAddSingleton<IOptionsMonitor<ModelCapabilitiesOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<ModelCapabilitiesOptions>>());
        services.TryAddSingleton<IOptions<ModelCapabilitiesOptions>>(sp =>
            sp.GetRequiredService<ConfigSectionOptionsMonitor<ModelCapabilitiesOptions>>());

        services.TryAddSingleton<IModelCapabilitySourceRegistry, ModelCapabilitySourceRegistry>();

        // Replace Core 的 Null 实现
        services.RemoveAll<IModelCapabilityManager>();
        services.AddSingleton<IModelCapabilityManager, ModelCapabilityManager>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 触发构造，订阅 Registry / Options
        _ = services.GetRequiredService<IModelCapabilityManager>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static void TryRegisterConfigSection(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry registry)
            {
                registry.Register(new ConfigSectionMeta(
                    ModelCapabilitiesOptions.SectionName,
                    "seeing.json",
                    ConfigScope.Both,
                    typeof(ModelCapabilitiesOptions)));
                return;
            }
        }
    }
}
