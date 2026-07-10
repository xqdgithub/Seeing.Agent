namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区变更事件参数
/// </summary>
public class WorkspaceChangedEventArgs : EventArgs
{
    /// <summary>旧工作区路径</summary>
    public string OldWorkspace { get; init; } = "";
    
    /// <summary>新工作区路径</summary>
    public string NewWorkspace { get; init; } = "";
}

/// <summary>
/// 工作区路径提供者 - 统一管理所有配置目录的获取逻辑
/// <para>
/// 目录层级：
/// - 用户级：~/.seeing/（基础配置）
/// - 项目级：{WorkspaceRoot}/.seeing/（覆盖同名）
/// </para>
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>
    /// 获取工作区根目录（启动应用程序的目录/工作目录）
    /// </summary>
    string WorkspaceRoot { get; }

    /// <summary>更新工作区根目录</summary>
    void SetWorkspaceRoot(string workspaceRoot);

    /// <summary>
    /// 获取用户级 .seeing 目录路径
    /// </summary>
    string UserSeeingDirectory { get; }

    /// <summary>
    /// 获取项目级 .seeing 目录路径
    /// </summary>
    string ProjectSeeingDirectory { get; }

    /// <summary>
    /// 获取指定级别的配置目录路径
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <returns>配置目录路径</returns>
    string GetSeeingDirectory(ConfigLevel level);
    
    /// <summary>
    /// 工作区变更事件
    /// </summary>
    event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;
}

/// <summary>
/// 配置级别
/// </summary>
public enum ConfigLevel
{
    /// <summary>用户级：~/.seeing/</summary>
    User,

    /// <summary>项目级：{WorkspaceRoot}/.seeing/</summary>
    Project
}

/// <summary>
/// 工作区路径提供者实现
/// </summary>
public class WorkspaceProvider : IWorkspaceProvider
{
    private volatile string _workspaceRoot;
    private readonly object _lock = new();

    /// <summary>
    /// 创建工作区路径提供者
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录，默认为当前工作目录</param>
    public WorkspaceProvider(string? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    /// <inheritdoc/>
    public string WorkspaceRoot => _workspaceRoot;
    
    /// <inheritdoc/>
    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;

    /// <summary>更新工作区根目录（InitializeSeeingAgentAsync 时调用）</summary>
    public void SetWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspaceRoot}");
        
        string oldWorkspace;
        lock (_lock)
        {
            oldWorkspace = _workspaceRoot;
            _workspaceRoot = workspaceRoot;
        }
        
        // 触发事件（在锁外执行，避免死锁）
        if (!string.Equals(oldWorkspace, workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceRootChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = oldWorkspace,
                NewWorkspace = workspaceRoot
            });
        }
    }

    /// <inheritdoc/>
    public string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    /// <inheritdoc/>
    public string ProjectSeeingDirectory =>
        Path.Combine(_workspaceRoot, ".seeing");

    /// <inheritdoc/>
    public string GetSeeingDirectory(ConfigLevel level) => level switch
    {
        ConfigLevel.User => UserSeeingDirectory,
        ConfigLevel.Project => ProjectSeeingDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
