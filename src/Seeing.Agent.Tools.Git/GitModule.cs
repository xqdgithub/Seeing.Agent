using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Git;

/// <summary>
/// Git 工具模块 — 提供 git_status/git_diff/git_log/git_commit。
/// </summary>
public sealed class GitModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedTools =
    [
        "git_status",
        "git_diff",
        "git_log",
        "git_commit",
    ];

    private static readonly IReadOnlyList<string> s_dependsOn = ["io.local"];

    /// <inheritdoc />
    public string Id => "git";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // Interim: register IGitService + ITool implementations so existing ToolManager discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<IOptions<GitOptions>>(_ => Options.Create(new GitOptions()));
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<ITool, GitStatusTool>();
        services.AddSingleton<ITool, GitDiffTool>();
        services.AddSingleton<ITool, GitLogTool>();
        services.AddSingleton<ITool, GitCommitTool>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
