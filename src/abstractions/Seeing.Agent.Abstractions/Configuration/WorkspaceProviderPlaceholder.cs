namespace Seeing.Agent.Configuration;

/// <summary>
/// 「工作区尚未选定」占位目录的唯一识别点。
/// <para>
/// 为什么需要占位目录而不是抛异常：Generic Host 在 <c>StartAsync</c> 会构造全部托管服务，
/// 其中有大量单例在构造时拷贝项目路径（会话存储、渠道状态、复盘根、技能根……）。
/// 有界面宿主可能停在「尚未选定工作区」的首页，抛异常会让这个合法状态变成启动失败。
/// </para>
/// <para>
/// 但占位目录绝不能当真实工作区：<see cref="WorkspaceProviderExtensions.GetProjectRoot"/>
/// 对它抛异常，业务代码用 <see cref="IWorkspaceProvider.IsResolved"/> 判断可用性。
/// </para>
/// </summary>
public static class WorkspaceProviderPlaceholder
{
    private static readonly string s_root = Path.Combine(
        Path.GetTempPath(), "seeing-pending-workspace", "project", ".seeing");

    /// <summary>尚未选定工作区时使用的占位项目目录（<c>{temp}/seeing-pending-workspace/project/.seeing</c>）。</summary>
    public static string ProjectSeeingDirectory => s_root;

    /// <summary>判断给定项目目录是否是占位目录。</summary>
    public static bool IsPlaceholder(string? projectSeeingDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectSeeingDirectory))
            return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectSeeingDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(s_root)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
