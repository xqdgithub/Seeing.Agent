using Seeing.Agent.Configuration;
using Seeing.Agent.Hosting.Execution;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 工作区切换的界面侧入口：转发到 <see cref="IWorkspaceSwitcher"/>，不再自行实现重载。
/// </summary>
public sealed class WorkspaceSwitchService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceSwitcher _switcher;

    public WorkspaceSwitchService(IWorkspaceProvider workspace, IWorkspaceSwitcher switcher)
    {
        _workspace = workspace;
        _switcher = switcher;
    }

    /// <summary>切换工作区（唯一的 Apply 路径）。</summary>
    public Task<WorkspaceApplyResult> ApplyAsync(string path, CancellationToken ct = default) =>
        _switcher.ApplyAsync(new WorkspaceApplyRequest(path), ct);

    /// <summary>当前工作区路径；未选定时为「(未选定)」。</summary>
    public string CurrentWorkspace => _workspace.IsResolved ? _workspace.ResolvedRoot! : "(未选定)";

    /// <summary>启动目录。</summary>
    public string StartupDirectory => _workspace.StartupDirectory;

    /// <summary>当前工作区来源。</summary>
    public WorkspaceResolutionSource ResolutionSource => _workspace.ResolutionSource;

    /// <summary>来源的中文描述。</summary>
    public string ResolutionSourceDescription => _workspace.ResolutionSource switch
    {
        WorkspaceResolutionSource.EnvironmentVariable => "环境变量",
        WorkspaceResolutionSource.ProjectCustomPath => "项目自定义路径",
        WorkspaceResolutionSource.GlobalDefault => "上次在界面里选定",
        WorkspaceResolutionSource.StartupDirectory => "启动目录",
        WorkspaceResolutionSource.ManualSwitch => "本次切换",
        _ => "未知"
    };

    /// <summary>保存项目级工作区选项（当前只剩文件硬边界；不再决定启动来源）。</summary>
    public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken ct = default) =>
        _workspace.SetWorkspaceOptionsAsync(options, ct);

    /// <summary>验证目录是否可以成为工作区。</summary>
    public (bool Valid, string? Error) ValidateWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "路径不能为空");

        if (!Directory.Exists(path))
            return (false, "目录不存在");

        return (true, null);
    }
}
