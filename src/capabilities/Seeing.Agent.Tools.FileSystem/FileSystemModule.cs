using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core.Tools.FileSystem;

/// <summary>
/// 文件系统工具模块 — 提供 read/write/edit/glob/grep/delete/add_workspace_path。
/// </summary>
public sealed class FileSystemModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "read",
        "write",
        "edit",
        "glob",
        "grep",
        "delete",
        "add_workspace_path",
    ];

    private static readonly IReadOnlyList<string> s_dependsOn = ["io.local"];

    /// <inheritdoc />
    public string Id => "filesystem";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ReadTool>();
        services.AddSingleton<WriteTool>();
        services.AddSingleton<EditTool>();
        services.AddSingleton<GlobTool>();
        services.AddSingleton<GrepTool>();
        services.AddSingleton<AddWorkspacePathTool>();
        services.AddSingleton<DeleteTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<ReadTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<WriteTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<EditTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<GlobTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<GrepTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<AddWorkspacePathTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<DeleteTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
