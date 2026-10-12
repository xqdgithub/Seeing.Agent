using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.SiliconFlow;

/// <summary>
/// SiliconFlow Provider 模块 — id=<c>provider.siliconflow</c>；依赖 <c>llm.openai</c> 工厂。
/// Activate 登记到 <see cref="IProviderRegistry"/>，Deactivate 注销。
/// </summary>
public sealed class SiliconFlowLlmModule : ISeeingModule
{
    public const string ModuleIdValue = "provider.siliconflow";

    private static readonly IReadOnlyList<string> s_dependsOn = ["llm.openai"];

    /// <inheritdoc />
    public string Id => ModuleIdValue;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            new SiliconFlowConfigStore(
                sp.GetService<IWorkspaceProvider>(),
                sp.GetRequiredService<ILogger<SiliconFlowConfigStore>>()));
        services.TryAddSingleton<SiliconFlowModelsClient>();
        services.TryAddSingleton<SiliconFlowProvider>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var provider = services.GetRequiredService<SiliconFlowProvider>();
        var registry = services.GetRequiredService<IProviderRegistry>();
        await provider.WarmupAsync(cancellationToken).ConfigureAwait(false);
        registry.Register(provider, SiliconFlowProvider.ExtensionId);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = services.GetRequiredService<SiliconFlowProvider>();
        services.GetRequiredService<IProviderRegistry>().Unregister(provider.Id);
        return Task.CompletedTask;
    }
}
