namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 定位随 CLI 构建/发布产出的服务程序集（WebUI / Gateway / TUI）。
/// 查找顺序：CLI 自身目录 → 其上级目录 → 仓库内对应项目的 bin 产物目录（开发期）。
/// </summary>
internal static class ServiceAssetLocator
{
    public const string WebUiDll = "Seeing.Agent.WebUI.dll";
    public const string GatewayDll = "Seeing.Gateway.Server.dll";
    public const string TuiDll = "seeing-tui.dll";

    internal const string SolutionFileName = "Seeing.Agent.slnx";
    internal const int MaxRepoRootLevels = 8;

    public static string Find(string cliDirectory, string dllName)
    {
        var sameDir = Path.Combine(cliDirectory, dllName);
        if (File.Exists(sameDir))
            return sameDir;

        var parentDir = Path.GetFullPath(Path.Combine(cliDirectory, "..", dllName));
        if (File.Exists(parentDir))
            return parentDir;

        var found = TryFindInRepo(cliDirectory, dllName);
        if (found is not null)
            return found;

        throw new FileNotFoundException(
            $"找不到 {dllName}。请先执行 dotnet build Seeing.Agent.slnx，或在发布目录中运行 seeing-cli。");
    }

    /// <summary>程序集名 → 仓库内项目目录名（二者并非总是同名，如 seeing-tui → Seeing.Agent.Tui）。</summary>
    public static string ResolveProjectDirectoryName(string dllName) => dllName switch
    {
        TuiDll => "Seeing.Agent.Tui",
        _ => dllName.Replace(".dll", string.Empty),
    };

    /// <summary>自 <paramref name="startDirectory"/> 逐级向上查找含解决方案文件的仓库根。</summary>
    public static string? TryFindRepoRoot(string startDirectory, int maxLevels = MaxRepoRootLevels)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch
        {
            return null;
        }

        for (var level = 0; level < maxLevels && dir is not null; level++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                return dir.FullName;
        }

        return null;
    }

    /// <summary>
    /// 开发期回退：在仓库内对应项目的 bin 目录中查找产物。
    /// 优先命中与 CLI 自身相同的配置目录（bin/Debug/net10.0），避免选中陈旧配置的产物；
    /// 否则取最近构建的一份，保证结果确定。
    /// </summary>
    private static string? TryFindInRepo(string cliDirectory, string dllName)
    {
        var repoRoot = TryFindRepoRoot(cliDirectory);
        if (repoRoot is null)
            return null;

        var binDirectory = Path.Combine(
            repoRoot, "samples", ResolveProjectDirectoryName(dllName), "bin");
        if (!Directory.Exists(binDirectory))
            return null;

        try
        {
            var candidates = Directory
                .EnumerateFiles(binDirectory, dllName, SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToList();
            if (candidates.Count == 0)
                return null;

            var preferredConfig = ResolveConfigurationSegment(cliDirectory);
            if (preferredConfig is not null)
            {
                var sameConfig = candidates
                    .Where(f => f.DirectoryName is not null
                        && f.DirectoryName.Contains(preferredConfig, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (sameConfig is not null)
                    return sameConfig.FullName;
            }

            return candidates
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .First()
                .FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 `<...>/bin/&lt;Configuration&gt;/&lt;TFM&gt;` 形式的目录中取出配置段（Debug/Release/…）。</summary>
    private static string? ResolveConfigurationSegment(string directory)
    {
        var parts = Path.GetFullPath(directory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = parts.Length - 2; i >= 1; i--)
        {
            if (string.Equals(parts[i - 1], "bin", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parts[i]))
            {
                return parts[i];
            }
        }

        return null;
    }
}
