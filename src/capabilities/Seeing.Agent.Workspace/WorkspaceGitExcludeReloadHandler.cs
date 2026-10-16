using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Workspace;

/// <summary>
/// 工作区切换重载处理器：工作区根变更后，对新工作区的 <c>.seeing</c> 重新应用 git 本地排除。
/// </summary>
public sealed class WorkspaceGitExcludeReloadHandler : IReloadHandler
{
    private readonly WorkspaceGitExcluder _excluder;
    private readonly ILogger<WorkspaceGitExcludeReloadHandler> _logger;

    /// <summary>创建处理器。</summary>
    public WorkspaceGitExcludeReloadHandler(
        WorkspaceGitExcluder excluder,
        ILogger<WorkspaceGitExcludeReloadHandler> logger)
    {
        _excluder = excluder ?? throw new ArgumentNullException(nameof(excluder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ComponentId => "workspace.git-exclude";

    /// <inheritdoc />
    public IReadOnlyList<Type> ChangeTypes { get; } = [typeof(WorkspaceChange)];

    /// <inheritdoc />
    public async Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
    {
        if (change is not WorkspaceChange workspace || string.IsNullOrWhiteSpace(workspace.NewWorkspace))
            return;

        try
        {
            var outcome = await _excluder
                .EnsureExcludedAsync(workspace.NewWorkspace, ct)
                .ConfigureAwait(false);
            _logger.LogDebug("工作区切换后应用 .seeing git 排除: {Workspace} -> {Outcome}",
                workspace.NewWorkspace, outcome);
        }
        catch (Exception ex)
        {
            // 排除属 best-effort 卫生行为，绝不阻断工作区切换
            _logger.LogWarning(ex, "工作区切换后应用 .seeing git 排除失败: {Workspace}", workspace.NewWorkspace);
        }
    }
}
