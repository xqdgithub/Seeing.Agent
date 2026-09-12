using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Llm.ModelCatalog.Builtin;

/// <summary>
/// 内置精简能力目录模块 — id=<c>llm.modelcatalog.builtin</c>；DependsOn llm.modelcapabilities。
/// 不含 models.dev 远程拉取。
/// </summary>
public sealed class BuiltinCatalogModule : ISeeingModule
{
    public string Id => "llm.modelcatalog.builtin";

    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    public IReadOnlyList<string> DependsOn { get; } = ["llm.modelcapabilities"];

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var dirs = sp.GetRequiredService<ISeeingDirectories>();
            var logger = sp.GetService<ILogger<BuiltinCapabilitySource>>();
            return new BuiltinCapabilitySource(dirs, logger);
        });
    }

    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = services.GetRequiredService<BuiltinCapabilitySource>();
        await source.LoadAsync(cancellationToken).ConfigureAwait(false);

        var registry = services.GetRequiredService<IModelCapabilitySourceRegistry>();
        registry.Register(source);
    }

    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registry = services.GetService<IModelCapabilitySourceRegistry>();
        registry?.Unregister(BuiltinCapabilitySource.SourceId);
        return Task.CompletedTask;
    }
}
