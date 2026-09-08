namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 路径边界检查（脊柱侧副本，不依赖 Tools.FileSystem 能力包）。
/// </summary>
internal static class PathSafety
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// 判断 <paramref name="path"/> 是否位于 <paramref name="baseDirectory"/> 之内（含相等）。
    /// </summary>
    public static bool IsPathWithinDirectory(string path, string baseDirectory)
    {
        try
        {
            var fullPath = ResolveRealPath(Path.GetFullPath(path));
            var fullBase = ResolveRealPath(Path.GetFullPath(baseDirectory));

            if (string.Equals(fullPath, fullBase, PathComparison))
                return true;

            var prefix = fullBase.EndsWith(Path.DirectorySeparatorChar) || fullBase.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(prefix, PathComparison);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveRealPath(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                var info = new DirectoryInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? info.FullName;
            }
            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? info.FullName;
            }

            var parent = Directory.GetParent(fullPath);
            if (parent == null) return fullPath;
            var resolvedParent = ResolveRealPath(parent.FullName);
            return Path.Combine(resolvedParent, Path.GetFileName(fullPath));
        }
        catch
        {
            return fullPath;
        }
    }
}
