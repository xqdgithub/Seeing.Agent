using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 工作区路径提供者实现
/// <para>
/// <b>未决议是合法状态</b>：有界面的宿主可能先显示工作区首页，等用户选定后才装载项目。
/// 未决议时 <see cref="ProjectSeeingDirectory"/> 必须失败，否则会话库、调度库、
/// 渠道状态目录会在构造单例时把「启动目录」当成真实工作区钉死。
/// </para>
/// </summary>
public class WorkspaceProvider : IWorkspaceProvider
{
    private volatile string? _workspaceRoot;
    private readonly string _startupDirectory;
    private string? _globalWorkspaceRoot;
    private WorkspaceResolutionSource _resolutionSource;
    private WorkspaceOptions _workspaceOptions = new();

    // 依赖服务（延迟注入）
    private UnifiedConfigManager? _configManager;
    private ILogger<WorkspaceProvider>? _logger;

    /// <summary>
    /// 创建工作区路径提供者（兼容语义：以进程当前目录为根）。
    /// <para>
    /// 需要「等启动决议」的宿主请用 <see cref="CreateUnresolved"/>：那种情况下
    /// 未选定时 <see cref="IsResolved"/> 为 false，项目路径是占位目录。
    /// </para>
    /// </summary>
    public WorkspaceProvider()
    {
        _startupDirectory = Directory.GetCurrentDirectory();
        _workspaceRoot = _startupDirectory;
        _resolutionSource = WorkspaceResolutionSource.StartupDirectory;
    }

    /// <summary>
    /// 创建「尚未选定工作区」的提供者。
    /// <para>
    /// 有界面宿主（以及走启动决议的 CLI/TUI/Gateway）用它：工作区由
    /// <see cref="SetInitialWorkspace"/> 决定，用户没选定之前显示工作区首页。
    /// </para>
    /// </summary>
    public static WorkspaceProvider CreateUnresolved()
    {
        var provider = new WorkspaceProvider();
        provider._workspaceRoot = null;
        return provider;
    }

    /// <summary>
    /// 创建工作区路径提供者并立即决议（测试与 ChannelHost 子进程使用）。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录；不存在时抛 <see cref="DirectoryNotFoundException"/>。</param>
    public WorkspaceProvider(string? workspaceRoot) : this()
    {
        if (string.IsNullOrEmpty(workspaceRoot))
            return;

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"工作区目录不存在: {workspaceRoot}");

        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
    }

    /// <summary>设置依赖服务（由 DI 容器在初始化时调用）</summary>
    internal void SetDependencies(
        UnifiedConfigManager configManager,
        ILogger<WorkspaceProvider>? logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string StartupDirectory => _startupDirectory;

    /// <inheritdoc/>
    public WorkspaceResolutionSource ResolutionSource => _resolutionSource;

    /// <inheritdoc/>
    public string? GlobalWorkspaceRoot => _globalWorkspaceRoot;

    /// <inheritdoc/>
    public bool IsResolved => _workspaceRoot is not null;

    /// <inheritdoc/>
    public string? ResolvedRoot => _workspaceRoot;

    /// <inheritdoc/>
    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;

    /// <summary>按启动决议设定工作区根（进程内首次选定）。</summary>
    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <param name="source">决议来源，用于设置页面展示与日志。</param>
    public void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("工作区根不能为空", nameof(workspaceRoot));

        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"工作区目录不存在: {workspaceRoot}");

        SetWorkspaceRootInternal(workspaceRoot, source);
    }

    /// <summary>更新工作区根目录（运行时切换，不持久化）</summary>
    public void SetWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspaceRoot}");

        SetWorkspaceRootInternal(workspaceRoot, WorkspaceResolutionSource.ManualSwitch);
    }

    /// <inheritdoc/>
    public string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    /// <inheritdoc/>
    /// <remarks>
    /// 工作区未选定时返回到 <c>%TEMP%\seeing-pending-workspace</c> 下的占位目录，而不是抛异常。
    /// <para>
    /// 为什么不抛：大量单例在构造时就拷贝项目路径（会话存储、渠道状态、复盘根、技能根……），
    /// 而 Generic Host 在 <c>StartAsync</c> 会构造全部托管服务。抛异常会让「停在首页」这一
    /// 合法状态直接变成启动失败。占位目录没有任何项目数据，且 <see cref="IsResolved"/> 为 false
    /// 时业务代码不得把占位目录当作真实工作区；首次 Apply 后由重载处理器重定位。
    /// </para>
    /// </remarks>
    public string ProjectSeeingDirectory
    {
        get
        {
            var root = _workspaceRoot
                ?? Path.Combine(Path.GetTempPath(), "seeing-pending-workspace", "project");
            return Path.Combine(root, ".seeing");
        }
    }

    /// <inheritdoc/>
    public string GetSeeingDirectory(ConfigLevel level) => level switch
    {
        ConfigLevel.User => UserSeeingDirectory,
        ConfigLevel.Project => ProjectSeeingDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    /// <summary>
    /// 兼容入口：决议已由宿主按 <c>WorkspaceLaunchPolicy</c> 完成，本方法不再自行解析工作区。
    /// <para>
    /// 环境变量 / <c>Workspace.CustomPath</c> / <c>Workspace.UseGlobal</c> /
    /// <c>GlobalWorkspaceRoot</c> 已退出启动选择（见
    /// docs/superpowers/specs/2026-11-07-workspace-home-design.md §3）。
    /// </para>
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsResolved)
        {
            _logger?.LogInformation("工作区未选定：等待用户在首页选择");
            return Task.CompletedTask;
        }

        // 读取用户级设置里的全局目录仅用于展示，不参与决议
        if (_configManager != null)
        {
            try
            {
                _globalWorkspaceRoot = _configManager.SeeingAgent.GlobalWorkspaceRoot;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "加载用户级全局工作区设置失败");
            }
        }

        _logger?.LogInformation("工作区来源: {Source}, 路径: {Path}", _resolutionSource, _workspaceRoot);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (_configManager == null)
            throw new InvalidOperationException("UnifiedConfigManager 未注入");

        await _configManager.SaveSectionAsync("GlobalWorkspaceRoot", path ?? (string)null!, ConfigLevel.User, cancellationToken);

        _globalWorkspaceRoot = path;
        _logger?.LogInformation("已保存全局工作区: {Path}", path ?? "(空)");
    }

    /// <inheritdoc/>
    public async Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
    {
        if (_configManager == null)
            throw new InvalidOperationException("UnifiedConfigManager 未注入");

        await _configManager.SaveSectionAsync("Workspace", options, ConfigLevel.Project, cancellationToken);
        _workspaceOptions = options;

        // 不再调用 InitializeAsync：那会把一次运行中的切换打回启动目录。
        _logger?.LogInformation("已保存项目工作区配置: RestrictToWorkspace={RestrictToWorkspace}",
            options.RestrictToWorkspace);
    }

    private void SetWorkspaceRootInternal(string path, WorkspaceResolutionSource source)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!Directory.Exists(path))
        {
            _logger?.LogWarning("工作区目录不存在: {Path}", path);
            return;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var oldWorkspace = _workspaceRoot;
        var oldSource = _resolutionSource;

        _workspaceRoot = normalized;
        _resolutionSource = source;

        // 同步进程 cwd，使 LocalExecutionWorld.Cwd 跟随工作区切换
        try
        {
            Directory.SetCurrentDirectory(normalized);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "无法将进程当前目录切换到工作区: {Path}", normalized);
        }

        _logger?.LogDebug("工作区已更新: {OldPath} -> {NewPath}, 来源: {OldSource} -> {NewSource}",
            oldWorkspace, normalized, oldSource, source);

        if (!IsSameDirectory(oldWorkspace, normalized))
        {
            WorkspaceRootChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = oldWorkspace ?? string.Empty,
                NewWorkspace = normalized
            });
        }
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (left is null || right is null)
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
    }
}
