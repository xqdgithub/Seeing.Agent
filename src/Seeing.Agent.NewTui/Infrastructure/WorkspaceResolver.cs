namespace Seeing.Agent.NewTui.Infrastructure;

/// <summary>
/// 解析 TUI 工作区根目录（CLI 参数、当前目录、向上查找标记目录）。
/// </summary>
public static class WorkspaceResolver
{
    private static readonly string[] RootMarkers = [".git", ".seeing", ".cursor"];

    /// <summary>
    /// 将用户传入路径解析为绝对路径；若未传入则使用当前目录并可选向上查找根。
    /// </summary>
    public static string Resolve(string? pathFromArgs, bool walkUpForRoot)
    {
        var start = string.IsNullOrWhiteSpace(pathFromArgs)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(pathFromArgs.Trim());

        if (!Directory.Exists(start))
            throw new DirectoryNotFoundException($"工作目录不存在: {start}");

        if (!walkUpForRoot)
            return start;

        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (RootMarkers.Any(m => File.Exists(Path.Combine(dir.FullName, m))
                                     || Directory.Exists(Path.Combine(dir.FullName, m))))
                return dir.FullName;

            dir = dir.Parent;
        }

        return start;
    }
}