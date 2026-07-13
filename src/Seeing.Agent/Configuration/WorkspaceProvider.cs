using Microsoft.Extensions.Logging;

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
/// 工作区解析来源
/// </summary>
public enum WorkspaceResolutionSource
{
    /// <summary>环境变量 SEEING_WORKSPACE_ROOT</summary>
    EnvironmentVariable,
    
    /// <summary>项目级自定义路径</summary>
    ProjectCustomPath,
    
    /// <summary>全局默认工作区</summary>
    GlobalDefault,
    
    /// <summary>启动目录（默认行为）</summary>
    StartupDirectory,
    
    /// <summary>手动切换（运行时）</summary>
    ManualSwitch
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
    /// 获取工作区根目录（唯一的工作目录获取入口）
    /// </summary>
    string WorkspaceRoot { get; }

    /// <summary>更新工作区根目录（运行时临时切换，不持久化）</summary>
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
    
    // === 新增成员 ===
    
    /// <summary>启动目录（程序启动时的当前目录，不变）</summary>
    string StartupDirectory { get; }
    
    /// <summary>当前工作区来源</summary>
    WorkspaceResolutionSource ResolutionSource { get; }
    
    /// <summary>全局默认工作区（用户级设置）</summary>
    string? GlobalWorkspaceRoot { get; }
    
    /// <summary>初始化工作区（根据配置解析）</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>设置全局默认工作区（持久化到用户级）</summary>
    Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default);
    
    /// <summary>设置项目级工作区配置（持久化到项目级）</summary>
    Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default);
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
    private readonly string _startupDirectory;
    private string? _globalWorkspaceRoot;
    private WorkspaceResolutionSource _resolutionSource;
    private WorkspaceOptions _workspaceOptions = new();
    private readonly object _lock = new();
    
    // 依赖服务（延迟注入）
    private IConfigurationPersistence? _persistence;
    private UnifiedConfigManager? _configManager;
    private ILogger<WorkspaceProvider>? _logger;

    /// <summary>
    /// 创建工作区路径提供者
    /// </summary>
    public WorkspaceProvider()
    {
        _startupDirectory = Directory.GetCurrentDirectory();
        _workspaceRoot = _startupDirectory;
        _resolutionSource = WorkspaceResolutionSource.StartupDirectory;
    }

    /// <summary>
    /// 创建工作区路径提供者（兼容旧代码）
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录</param>
    public WorkspaceProvider(string? workspaceRoot) : this()
    {
        if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
        {
            _workspaceRoot = workspaceRoot;
        }
    }

    /// <summary>设置依赖服务（由 DI 容器在初始化时调用）</summary>
    internal void SetDependencies(
        IConfigurationPersistence persistence,
        UnifiedConfigManager configManager,
        ILogger<WorkspaceProvider>? logger)
    {
        _persistence = persistence;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string WorkspaceRoot => _workspaceRoot;
    
    /// <inheritdoc/>
    public string StartupDirectory => _startupDirectory;
    
    /// <inheritdoc/>
    public WorkspaceResolutionSource ResolutionSource => _resolutionSource;
    
    /// <inheritdoc/>
    public string? GlobalWorkspaceRoot => _globalWorkspaceRoot;
    
    /// <inheritdoc/>
    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;

    /// <summary>更新工作区根目录（运行时临时切换，不持久化）</summary>
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
    public string ProjectSeeingDirectory =>
        Path.Combine(_workspaceRoot, ".seeing");

    /// <inheritdoc/>
    public string GetSeeingDirectory(ConfigLevel level) => level switch
    {
        ConfigLevel.User => UserSeeingDirectory,
        ConfigLevel.Project => ProjectSeeingDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1. 环境变量优先级最高
        var envWorkspace = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(envWorkspace) && Directory.Exists(envWorkspace))
        {
            SetWorkspaceRootInternal(envWorkspace, WorkspaceResolutionSource.EnvironmentVariable);
            _logger?.LogInformation("工作区来源: 环境变量, 路径: {Path}", envWorkspace);
            return;
        }
        
        // 2. 加载用户级设置（全局工作区）
        if (_persistence != null)
        {
            try
            {
                var userSettings = await _persistence.LoadAsync(cancellationToken);
                _globalWorkspaceRoot = userSettings.GlobalWorkspaceRoot;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "加载用户级设置失败");
            }
        }
        
        // 3. 加载项目级配置
        if (_configManager != null)
        {
            try
            {
                var options = _configManager.GetSection<WorkspaceOptions>("Workspace");
                if (options != null)
                    _workspaceOptions = options;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "加载项目级工作区配置失败");
            }
        }
        
        // 4. 项目级自定义路径
        if (!string.IsNullOrEmpty(_workspaceOptions.CustomPath))
        {
            if (Directory.Exists(_workspaceOptions.CustomPath))
            {
                SetWorkspaceRootInternal(_workspaceOptions.CustomPath, WorkspaceResolutionSource.ProjectCustomPath);
                _logger?.LogInformation("工作区来源: 项目自定义路径, 路径: {Path}", _workspaceOptions.CustomPath);
                return;
            }
            else
            {
                _logger?.LogWarning("项目自定义路径不存在: {Path}, 回退到默认行为", _workspaceOptions.CustomPath);
            }
        }
        
        // 5. 检查是否使用全局默认
        if (_workspaceOptions.UseGlobal)
        {
            if (!string.IsNullOrEmpty(_globalWorkspaceRoot) && Directory.Exists(_globalWorkspaceRoot))
            {
                SetWorkspaceRootInternal(_globalWorkspaceRoot, WorkspaceResolutionSource.GlobalDefault);
                _logger?.LogInformation("工作区来源: 全局默认, 路径: {Path}", _globalWorkspaceRoot);
                return;
            }
            else
            {
                _logger?.LogWarning("启用了全局工作区但未配置有效路径，回退到启动目录");
            }
        }
        
        // 6. 默认行为：使用启动目录
        SetWorkspaceRootInternal(_startupDirectory, WorkspaceResolutionSource.StartupDirectory);
        _logger?.LogInformation("工作区来源: 启动目录, 路径: {Path}", _startupDirectory);
    }

    /// <inheritdoc/>
    public async Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (_persistence == null)
            throw new InvalidOperationException("ConfigurationPersistence 未注入");
        
        var settings = await _persistence.LoadAsync(cancellationToken);
        settings.GlobalWorkspaceRoot = path;
        await _persistence.SaveAsync(settings, cancellationToken);
        
        _globalWorkspaceRoot = path;
        _logger?.LogInformation("已保存全局工作区: {Path}", path ?? "(空)");
        
        // 如果当前使用的是全局工作区，更新工作区
        if (_resolutionSource == WorkspaceResolutionSource.GlobalDefault && 
            !string.IsNullOrEmpty(path) && 
            Directory.Exists(path))
        {
            SetWorkspaceRootInternal(path, WorkspaceResolutionSource.GlobalDefault);
        }
    }

    /// <inheritdoc/>
    public async Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
    {
        if (_configManager == null)
            throw new InvalidOperationException("UnifiedConfigManager 未注入");
        
        await _configManager.SaveSectionAsync("Workspace", options, ConfigLevel.Project, cancellationToken);
        _workspaceOptions = options;
        _logger?.LogInformation("已保存项目工作区配置: UseGlobal={UseGlobal}, CustomPath={CustomPath}", 
            options.UseGlobal, options.CustomPath ?? "(空)");
        
        // 重新初始化以应用新配置
        await InitializeAsync(cancellationToken);
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
        
        var oldWorkspace = _workspaceRoot;
        var oldSource = _resolutionSource;
        
        _workspaceRoot = path;
        _resolutionSource = source;
        
        _logger?.LogDebug("工作区已更新: {OldPath} -> {NewPath}, 来源: {OldSource} -> {NewSource}", 
            oldWorkspace, path, oldSource, source);
        
        if (!string.Equals(oldWorkspace, path, StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceRootChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = oldWorkspace,
                NewWorkspace = path
            });
        }
    }
}
