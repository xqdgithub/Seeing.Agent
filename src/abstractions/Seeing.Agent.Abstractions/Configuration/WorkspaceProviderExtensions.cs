namespace Seeing.Agent.Configuration;

/// <summary>
/// <see cref="IWorkspaceProvider"/> 辅助方法。
/// 项目根从 <see cref="IWorkspaceProvider.ProjectSeeingDirectory"/> 推导；
/// 执行 cwd 请使用 <c>IExecutionWorld.Cwd</c>，勿再经工作区提供者取 cwd。
/// </summary>
public static class WorkspaceProviderExtensions
{
    /// <summary>
    /// 项目根目录（配置归属根，即 <c>{root}/.seeing</c> 的父目录）。
    /// 非工具执行 cwd。
    /// </summary>
    /// <exception cref="InvalidOperationException">工作区尚未选定（占位目录不是工作区）。</exception>
    public static string GetProjectRoot(this IWorkspaceProvider workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var seeing = workspace.ProjectSeeingDirectory;

        // 未选定时 ProjectSeeingDirectory 是占位目录；把占位目录当项目根会让业务把数据写进去。
        // 用占位目录本身判断（而不是 IsResolved），使既有测试替身无需额外实现新成员。
        if (WorkspaceProviderPlaceholder.IsPlaceholder(seeing))
            throw new InvalidOperationException(
                "工作区尚未选定：须先 SetInitialWorkspace 或等待用户在工作区首页选定。");

        seeing = seeing.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(seeing);
        if (string.IsNullOrEmpty(parent))
            return seeing;
        // 保持与 ProjectSeeingDirectory 相同的路径形态（相对/绝对），避免无谓 GetFullPath
        return parent;
    }
}
