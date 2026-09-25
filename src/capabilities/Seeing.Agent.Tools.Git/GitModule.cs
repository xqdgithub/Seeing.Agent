using Microsoft.Extensions.DependencyInjection;
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
        // 不再用默认值覆盖注册：经 AddOptions 暴露 IOptionsMonitor，支持外部配置与热重载
        services.AddOptions<GitOptions>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<GitStatusTool>();
        services.AddSingleton<GitDiffTool>();
        services.AddSingleton<GitLogTool>();
        services.AddSingleton<GitCommitTool>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tm = services.GetRequiredService<IToolManager>();
        await tm.RegisterToolAsync(services.GetRequiredService<GitStatusTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<GitDiffTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<GitLogTool>(), cancellationToken).ConfigureAwait(false);
        await tm.RegisterToolAsync(services.GetRequiredService<GitCommitTool>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tm = services.GetRequiredService<IToolManager>();
        foreach (var id in ProvidedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
