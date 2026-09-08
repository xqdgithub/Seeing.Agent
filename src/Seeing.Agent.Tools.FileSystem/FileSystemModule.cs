using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.FileSystem;

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
        // Interim: register ITool implementations so existing ToolManager discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<ITool, ReadTool>();
        services.AddSingleton<ITool, WriteTool>();
        services.AddSingleton<ITool, EditTool>();
        services.AddSingleton<ITool, GlobTool>();
        services.AddSingleton<ITool, GrepTool>();
        services.AddSingleton<ITool, AddWorkspacePathTool>();
        services.AddSingleton<ITool, DeleteTool>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
