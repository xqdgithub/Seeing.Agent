namespace Seeing.Agent.WebUI.Services;

/// <summary>目录列表里的一项。</summary>
public sealed record DirectoryEntryView(string Name, string FullPath);

/// <summary>一次目录列表结果。<see cref="Error"/> 非空时 <see cref="Directories"/> 为空。</summary>
public sealed record DirectoryListing(
    string Path,
    string? ParentPath,
    IReadOnlyList<DirectoryEntryView> Directories,
    string? Error);

/// <summary>
/// 进程内的目录浏览。仅供 <c>DirectoryPicker</c> 使用。
/// </summary>
public interface IDirectoryBrowser
{
    /// <summary>可用的根：就绪的驱动器，外加用户主目录。</summary>
    IReadOnlyList<DirectoryEntryView> GetRoots();

    /// <summary>列出子目录。空路径表示从用户主目录开始。</summary>
    DirectoryListing List(string path);

    /// <summary>在已列出的父目录下新建文件夹。</summary>
    DirectoryListing CreateSubdirectory(string parentPath, string name);
}

/// <summary>
/// 进程内的目录浏览实现。
/// <para>
/// 为什么不复用 <c>Seeing.Agent.Files</c> / <c>WebUI.Files</c>：那些能力只描述当前工作区，
/// 而选新工作区时必须能看到工作区之外。这里不调用 <c>IWorkspacePathGate</c> 或 <c>PathBoundary</c>：
/// 它不判断「是否在根内」，所以不该走那道门；越界保护靠「父目录必须是本次已列出的目录」。
/// </para>
/// <para>
/// 信任模型与页面一致：能打开 WebUI 就能调用。WebUI 没有独立登录，
/// 部署到非回环地址时本服务与页面同面暴露；不单独加认证。
/// </para>
/// </summary>
public sealed class DirectoryBrowser : IDirectoryBrowser
{
    // 本次进程里已经列给用户的目录。创建只允许发生在这些目录之下。
    private readonly HashSet<string> _listed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyList<DirectoryEntryView> GetRoots()
    {
        var roots = new List<DirectoryEntryView>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (!ready)
                continue;

            var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            roots.Add(new DirectoryEntryView(name, drive.RootDirectory.FullName));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            var label = new DirectoryInfo(home).Name;
            if (roots.All(r => !IsSame(r.FullPath, home)))
                roots.Insert(0, new DirectoryEntryView($"主目录（{label}）", home));
        }

        return roots;
    }

    /// <inheritdoc/>
    public DirectoryListing List(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Directory.Exists(home) ? home : GetRoots().FirstOrDefault()?.FullPath ?? "/";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return Failed(path, $"路径无法识别: {ex.Message}");
        }

        if (!Directory.Exists(full))
            return Failed(full, "目录不存在");

        try
        {
            var entries = new DirectoryInfo(full)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.System) == 0)
                .Select(d => new DirectoryEntryView(d.Name, d.FullName))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MarkListed(full);

            return new DirectoryListing(full, ParentOf(full), entries, null);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(full, "没有访问权限");
        }
        catch (IOException ex)
        {
            return Failed(full, ex.Message);
        }
    }

    /// <inheritdoc/>
    public DirectoryListing CreateSubdirectory(string parentPath, string name)
    {
        if (!IsListed(parentPath))
            return Failed(parentPath, "只能在本对话框已列出的目录下新建文件夹");

        if (string.IsNullOrWhiteSpace(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name.Contains("..", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Failed(parentPath, "文件夹名称不合法");
        }

        var target = Path.Combine(parentPath, name.Trim());
        try
        {
            Directory.CreateDirectory(target);
            return List(parentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed(parentPath, $"新建失败: {ex.Message}");
        }
    }

    private void MarkListed(string path)
    {
        var key = Normalize(path);
        lock (_gate)
            _listed.Add(key);
    }

    private bool IsListed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string key;
        try
        {
            key = Normalize(path);
        }
        catch (Exception)
        {
            return false;
        }

        lock (_gate)
            return _listed.Contains(key);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static DirectoryListing Failed(string path, string error) =>
        new(path, null, Array.Empty<DirectoryEntryView>(), error);

    private static string? ParentOf(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
