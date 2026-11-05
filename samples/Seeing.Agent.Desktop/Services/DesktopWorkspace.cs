namespace Seeing.Agent.Desktop.Services;

/// <summary>桌面启动时工作区的来源，用于日志与排障。</summary>
public enum DesktopWorkspaceSource
{
    /// <summary>环境变量 <c>SEEING_WORKSPACE_ROOT</c>。</summary>
    EnvironmentVariable,

    /// <summary>进程当前目录（从终端/IDE 启动，或快捷方式指定了「起始位置」）。</summary>
    CurrentDirectory,

    /// <summary>应用默认目录（<c>%LOCALAPPDATA%\Seeing</c>）。</summary>
    ApplicationDefault,
}

/// <summary>工作区解析结果。</summary>
/// <param name="Path">最终选定的工作区绝对路径。</param>
/// <param name="Source">该路径的来源。</param>
public sealed record DesktopWorkspaceResolution(string Path, DesktopWorkspaceSource Source)
{
    /// <summary>来源的中文描述，用于启动日志。</summary>
    public string Describe() => Source switch
    {
        DesktopWorkspaceSource.EnvironmentVariable => "环境变量",
        DesktopWorkspaceSource.CurrentDirectory => "启动目录",
        DesktopWorkspaceSource.ApplicationDefault => "应用默认目录",
        _ => "未知",
    };
}

/// <summary>
/// 桌面宿主的启动位置解析。
/// <para>
/// 为什么桌面版需要单独处理：<c>WorkspaceProvider</c> 的兜底是「进程当前工作目录」，
/// 而桌面应用通常从资源管理器/快捷方式启动，此时 cwd 是 <b>exe 所在目录</b>
/// （安装目录，Windows 下常为 <c>Program Files</c>，不可写）。
/// Agent 会把会话库、记忆、文件写入工作区，落到那里会直接失败或写进安装目录。
/// </para>
/// <para>
/// 本类只负责把「进程起始位置」摆到一个合理的地方，<b>不决定工作区</b>。
/// 工作区仍由 <c>WorkspaceProvider.InitializeAsync</c> 按 Core 的既有优先级裁决：
/// 环境变量 → 项目级 <c>Workspace.CustomPath</c> → <c>Workspace.UseGlobal</c> 为真时的
/// 用户级 <c>GlobalWorkspaceRoot</c> → 启动目录（此处即本类选定的位置）。
/// </para>
/// <para>
/// 为什么不在桌面壳里直接读 <c>GlobalWorkspaceRoot</c>：那会绕过 <c>UseGlobal</c> 开关。
/// Core 的语义是「配置了全局目录也要显式 Use 才生效」；绕过去会让
/// 「配了全局目录但项目里选择不使用」的配置失效。
/// </para>
/// <para>
/// 解析顺序（先命中先返回）：
/// <list type="number">
/// <item>环境变量 <c>SEEING_WORKSPACE_ROOT</c> —— 用户显式指定，最高优先级。</item>
/// <item>进程当前目录，但<b>必须不是 exe 所在目录</b> —— 从终端/IDE 启动时保留用户显式意图。</item>
/// <item><c>%LOCALAPPDATA%\Seeing</c> —— 稳定、可写、不污染文档目录且不被 OneDrive 同步。</item>
/// </list>
/// </para>
/// </summary>
public static class DesktopWorkspace
{
    /// <summary>与 Core 一致的环境变量名。</summary>
    public const string EnvironmentVariableName = "SEEING_WORKSPACE_ROOT";

    /// <summary>应用默认工作区所在的用户本地应用数据目录下的文件夹名（产品名是 Seeing）。</summary>
    public const string DefaultFolderName = "Seeing";

    /// <summary>
    /// 解析启动工作区（纯函数，不做文件系统写入，便于测试）。
    /// </summary>
    /// <remarks>
    /// 只做「起始位置」解析；工作区最终由 Core 的 <c>WorkspaceProvider.InitializeAsync</c> 裁决
    /// （环境变量 / 项目 CustomPath / UseGlobal / 启动目录）。
    /// </remarks>
    /// <param name="currentDirectory">进程当前目录。</param>
    /// <param name="executableDirectory">exe 所在目录（<see cref="AppContext.BaseDirectory"/>）。</param>
    /// <param name="environmentWorkspace">环境变量值，可为 null。</param>
    /// <param name="localAppDataDirectory">用户本地应用数据目录（<c>%LOCALAPPDATA%</c> / <c>~/.local/share</c>）。</param>
    /// <param name="directoryExists">目录存在性判定（注入以便测试）。</param>
    public static DesktopWorkspaceResolution Resolve(
        string currentDirectory,
        string executableDirectory,
        string? environmentWorkspace,
        string localAppDataDirectory,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        if (IsNonEmpty(environmentWorkspace) && directoryExists(environmentWorkspace!))
            return new DesktopWorkspaceResolution(Normalize(environmentWorkspace!), DesktopWorkspaceSource.EnvironmentVariable);

        // cwd 可用当且仅当：它是个真实目录，且不是 exe 所在目录。
        // 后者正是「从资源管理器双击」的典型形态，不能当作工作区。
        if (IsNonEmpty(currentDirectory)
            && directoryExists(currentDirectory)
            && !IsSameDirectory(currentDirectory, executableDirectory))
        {
            return new DesktopWorkspaceResolution(Normalize(currentDirectory), DesktopWorkspaceSource.CurrentDirectory);
        }

        return new DesktopWorkspaceResolution(
            Path.Combine(Normalize(localAppDataDirectory), DefaultFolderName),
            DesktopWorkspaceSource.ApplicationDefault);
    }

    /// <summary>
    /// 解析并应用启动位置：必要时创建目录，并把进程当前目录切换过去。
    /// </summary>
    /// <returns>最终选定的起始位置（可能与解析结果不同，见 <paramref name="fallbackDirectory"/>）。</returns>
    public static DesktopWorkspaceResolution Apply(string? fallbackDirectory = null)
    {
        var currentDirectory = SafeGetCurrentDirectory();
        var executableDirectory = AppContext.BaseDirectory;

        var resolution = Resolve(
            currentDirectory,
            executableDirectory,
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Directory.Exists);

        var applied = EnsureDirectory(resolution, fallbackDirectory);

        if (!IsSameDirectory(applied.Path, currentDirectory))
        {
            try
            {
                Directory.SetCurrentDirectory(applied.Path);
            }
            catch (Exception)
            {
                // 切不过去不影响宿主启动：WorkspaceProvider 初始化时会再切一次，
                // 且各路径解析都以绝对路径为准。
            }
        }

        return applied;
    }

    /// <summary>确保目录存在；失败时退回到备用目录，两处都失败才抛。</summary>
    private static DesktopWorkspaceResolution EnsureDirectory(
        DesktopWorkspaceResolution resolution,
        string? fallbackDirectory)
    {
        try
        {
            Directory.CreateDirectory(resolution.Path);
            return resolution;
        }
        catch (Exception) when (fallbackDirectory is not null)
        {
            Directory.CreateDirectory(fallbackDirectory);
            return resolution with { Path = Normalize(fallbackDirectory) };
        }
    }

    private static string SafeGetCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool IsNonEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static bool IsSameDirectory(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        try
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
