using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Workspace;

/// <summary>
/// 工作区卫生模块 — id=<c>workspace.git-exclude</c>。
/// <para>
/// 启动（Activate）与工作区切换（<see cref="WorkspaceChange"/>）时，将工作区 <c>.seeing</c> 目录
/// 幂等加入所属 git 仓库的本地排除文件 <c>.git/info/exclude</c>；不修改 <c>.gitignore</c>，
/// 不影响协作者。非 git 仓库 / git 缺失时静默降级。
/// </para>
/// </summary>
public sealed class WorkspaceGitExcludeModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_dependsOn = ["io.local"];

    /// <inheritdoc />
    public string Id => "workspace.git-exclude";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn => s_dependsOn;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<WorkspaceGitExcluder>();
        services.TryAddSingleton<WorkspaceGitExcludeReloadHandler>();
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = services.GetService<ILogger<WorkspaceGitExcludeModule>>();

        // 1) 对当前工作区立即应用
        try
        {
            if (services.GetService<WorkspaceGitExcluder>() is { } excluder
                && services.GetService<IWorkspaceProvider>() is { } workspace)
            {
                var outcome = await excluder
                    .EnsureExcludedAsync(workspace.GetProjectRoot(), cancellationToken)
                    .ConfigureAwait(false);
                logger?.LogDebug("启动时应用 .seeing git 排除: {Outcome}", outcome);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "启动时应用 .seeing git 排除失败");
        }

        // 2) 工作区切换后重新应用
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<WorkspaceGitExcludeReloadHandler>() is { } handler)
        {
            registry.RegisterHandler(handler);
        }
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (services.GetService<IReloadHandlerRegistry>() is { } registry
            && services.GetService<WorkspaceGitExcludeReloadHandler>() is { } handler)
        {
            registry.UnregisterHandler(handler);
        }

        return Task.CompletedTask;
    }
}
