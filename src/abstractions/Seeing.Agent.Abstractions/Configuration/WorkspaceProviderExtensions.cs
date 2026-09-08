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
    public static string GetProjectRoot(this IWorkspaceProvider workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var seeing = workspace.ProjectSeeingDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(seeing);
        if (string.IsNullOrEmpty(parent))
            return seeing;
        // 保持与 ProjectSeeingDirectory 相同的路径形态（相对/绝对），避免无谓 GetFullPath
        return parent;
    }
}
