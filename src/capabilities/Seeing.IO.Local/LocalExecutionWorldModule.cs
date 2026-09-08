using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.IO.Local;

/// <summary>
/// 本地执行世界模块 — 提供 <c>executionWorld</c> seam。
/// </summary>
public sealed class LocalExecutionWorldModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["executionWorld"];

    /// <inheritdoc />
    public string Id => "io.local";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<IExecutionWorld, LocalExecutionWorld>();
        services.TryAddSingleton<IFileSystem>(sp => sp.GetRequiredService<IExecutionWorld>().FileSystem);
        services.TryAddSingleton<ISubprocessFactory>(sp => sp.GetRequiredService<IExecutionWorld>().Subprocess);
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
