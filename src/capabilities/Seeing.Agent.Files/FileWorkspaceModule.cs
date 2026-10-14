using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Files.Hosting;
using Seeing.Agent.Files.Watching;

namespace Seeing.Agent.Files;

/// <summary>文件工作区能力模块（无工具；提供 IFileWorkspaceFactory）。</summary>
public sealed class FileWorkspaceModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = ["io.local"];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<FileWatcherPool>();
        services.TryAddSingleton<IFileWorkspaceFactory, FileWorkspaceFactory>();
        services.AddModuleHostedService<FileWatcherHostedService>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
