using Seeing.Agent.Configuration;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 工作区切换服务 - 处理工作区变更及后续重载操作
/// <para>
/// 依赖服务通过订阅 IWorkspaceProvider.WorkspaceRootChanged 事件自行响应工作区变更。
/// </para>
/// </summary>
public class WorkspaceSwitchService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly SeeingConfigService _configService;
    private readonly SkillStateService _skillState;
    private readonly ToolStateService _toolState;
    private readonly ILogger<WorkspaceSwitchService> _logger;

    /// <summary>
    /// 工作区变更事件（向后兼容，建议使用 IWorkspaceProvider.WorkspaceRootChanged）
    /// </summary>
    [Obsolete("Use IWorkspaceProvider.WorkspaceRootChanged instead")]
    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

    public WorkspaceSwitchService(
        IWorkspaceProvider workspace,
        SeeingConfigService configService,
        SkillStateService skillState,
        ToolStateService toolState,
        ILogger<WorkspaceSwitchService> logger)
    {
        _workspace = workspace;
        _configService = configService;
        _skillState = skillState;
        _toolState = toolState;
        _logger = logger;
    }

    /// <summary>
    /// 切换工作区（运行时临时切换，不持久化）
    /// </summary>
    /// <param name="newWorkspaceRoot">新工作区路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>切换是否成功</returns>
    public async Task<bool> SwitchWorkspaceAsync(string newWorkspaceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newWorkspaceRoot))
        {
            _logger.LogWarning("工作区路径不能为空");
            return false;
        }

        if (!Directory.Exists(newWorkspaceRoot))
        {
            _logger.LogWarning("工作区目录不存在: {Path}", newWorkspaceRoot);
            return false;
        }

        var oldWorkspace = _workspace.WorkspaceRoot;
        if (string.Equals(oldWorkspace, newWorkspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("工作区未变更: {Path}", newWorkspaceRoot);
            return true;
        }

        try
        {
            _logger.LogInformation("切换工作区: {Old} -> {New}", oldWorkspace, newWorkspaceRoot);

            // 1. 更新工作区根目录（会触发 WorkspaceRootChanged 事件）
            _workspace.SetWorkspaceRoot(newWorkspaceRoot);

            // 2. 重新加载配置
            await _configService.ReloadAsync(cancellationToken);

            // 3. 重新加载 Skill 和 Tool 状态
            await _skillState.LoadAsync(cancellationToken);
            await _toolState.LoadAsync(cancellationToken);

            // 4. 触发遗留事件（向后兼容）
#pragma warning disable CS0618 // Type or member is obsolete
            WorkspaceChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = oldWorkspace,
                NewWorkspace = newWorkspaceRoot
            });
#pragma warning restore CS0618 // Type or member is obsolete

            _logger.LogInformation("工作区切换完成: {Path}", newWorkspaceRoot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换工作区失败: {Path}", newWorkspaceRoot);
            // 尝试回滚
            try
            {
                _workspace.SetWorkspaceRoot(oldWorkspace);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "回滚工作区失败");
            }
            return false;
        }
    }

    /// <summary>
    /// 设置全局默认工作区（持久化到用户级设置）
    /// </summary>
    public async Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
    {
        await _workspace.SetGlobalWorkspaceRootAsync(path, cancellationToken);
    }

    /// <summary>
    /// 设置项目级工作区配置（持久化到项目级配置）
    /// </summary>
    public async Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
    {
        await _workspace.SetWorkspaceOptionsAsync(options, cancellationToken);
        
        // 重新加载配置
        await _configService.ReloadAsync(cancellationToken);
        await _skillState.LoadAsync(cancellationToken);
        await _toolState.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// 获取当前工作区路径
    /// </summary>
    public string CurrentWorkspace => _workspace.WorkspaceRoot;

    /// <summary>
    /// 获取启动目录
    /// </summary>
    public string StartupDirectory => _workspace.StartupDirectory;

    /// <summary>
    /// 获取当前工作区来源
    /// </summary>
    public WorkspaceResolutionSource ResolutionSource => _workspace.ResolutionSource;

    /// <summary>
    /// 获取全局默认工作区
    /// </summary>
    public string? GlobalWorkspaceRoot => _workspace.GlobalWorkspaceRoot;

    /// <summary>
    /// 获取工作区来源描述
    /// </summary>
    public string ResolutionSourceDescription => _workspace.ResolutionSource switch
    {
        WorkspaceResolutionSource.EnvironmentVariable => "环境变量",
        WorkspaceResolutionSource.ProjectCustomPath => "项目自定义路径",
        WorkspaceResolutionSource.GlobalDefault => "全局默认",
        WorkspaceResolutionSource.StartupDirectory => "启动目录",
        WorkspaceResolutionSource.ManualSwitch => "手动切换",
        _ => "未知"
    };

    /// <summary>
    /// 验证工作区路径是否有效
    /// </summary>
    public (bool Valid, string? Error) ValidateWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "路径不能为空");

        if (!Directory.Exists(path))
            return (false, "目录不存在");

        // 检查是否为有效的工作区（可以包含 .seeing 目录或项目文件）
        var hasSeeingDir = Directory.Exists(Path.Combine(path, ".seeing"));
        var hasProjectFiles = Directory.GetFiles(path, "*.csproj").Length > 0 ||
                              Directory.GetFiles(path, "*.sln").Length > 0;

        if (!hasSeeingDir && !hasProjectFiles)
        {
            // 仍然允许，但给出警告
            _logger.LogDebug("目录不包含 .seeing 或项目文件: {Path}", path);
        }

        return (true, null);
    }
}
