using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

/// <summary>
/// models.dev 目录模块 — id=<c>llm.modelcatalog.modelsdev</c>；DependsOn llm.modelcapabilities。
/// </summary>
public sealed class ModelsDevCatalogModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "llm.modelcatalog.modelsdev";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = ["llm.modelcapabilities"];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<HttpClient>();
        services.TryAddSingleton(sp =>
        {
            var dirs = sp.GetRequiredService<ISeeingDirectories>();
            var logger = sp.GetService<ILogger<ModelsDevCapabilitySource>>();
            var http = sp.GetService<HttpClient>() ?? new HttpClient();
            return new ModelsDevCapabilitySource(dirs, logger, http);
        });
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = services.GetRequiredService<ModelsDevCapabilitySource>();
        await source.LoadAsync(cancellationToken).ConfigureAwait(false);

        var registry = services.GetRequiredService<IModelCapabilitySourceRegistry>();
        registry.Register(source);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registry = services.GetService<IModelCapabilitySourceRegistry>();
        registry?.Unregister(ModelsDevCapabilitySource.SourceId);
        return Task.CompletedTask;
    }
}
