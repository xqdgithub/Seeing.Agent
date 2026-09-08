namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// Resolves gateway plugin specs (file://, relative, ~, absolute) to assembly paths.
/// Extracted from deleted core ExtensionLoader — NuGet package download is not supported.
/// </summary>
internal static class PluginSpecPathResolver
{
    public static string Resolve(string spec)
    {
        if (IsFileSpec(spec))
            return ResolveFilePath(spec);

        throw new NotSupportedException(
            $"NuGet 扩展下载尚未实现，请使用文件路径方式: {spec}");
    }

    private static bool IsFileSpec(string spec) =>
        spec.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        spec.StartsWith("./", StringComparison.Ordinal) ||
        spec.StartsWith("../", StringComparison.Ordinal) ||
        spec.StartsWith("~", StringComparison.Ordinal) ||
        Path.IsPathRooted(spec);

    private static string ResolveFilePath(string spec)
    {
        var path = spec;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = new Uri(path).LocalPath;
        }
        else if (path.StartsWith("~", StringComparison.Ordinal))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path.Length == 1
                ? userProfile
                : Path.Combine(userProfile, path.Substring(1).TrimStart('/', '\\'));
        }

        if (!Path.IsPathRooted(path))
        {
            var cwdCandidate = Path.GetFullPath(path);
            if (File.Exists(cwdCandidate))
                return cwdCandidate;

            var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
            if (File.Exists(baseCandidate))
                return baseCandidate;

            path = cwdCandidate;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"Extension file not found: {path}");

        return path;
    }
}
