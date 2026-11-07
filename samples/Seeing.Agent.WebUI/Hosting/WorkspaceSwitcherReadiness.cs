using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Hosting.Execution;

namespace Seeing.Agent.WebUI.Hosting;

/// <summary>
/// 把「首次选定工作区」这件事接到补初始化上。
/// <para>
/// 宿主启动时如果还没选定工作区，<see cref="WorkspaceReadyInitializer"/> 不能先跑；
/// 这里订阅工作区根变更事件，第一次变更后补做一次（之后由各自的钩子/热重载负责）。
/// </para>
/// </summary>
public sealed class WorkspaceSwitcherReadiness
{
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<WorkspaceSwitcherReadiness> _logger;
    private int _attached;

    public WorkspaceSwitcherReadiness(
        IWorkspaceProvider workspace,
        ILogger<WorkspaceSwitcherReadiness> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>挂接首次选定的补初始化（幂等）。</summary>
    public void Attach(WorkspaceReadyInitializer initializer)
    {
        if (Interlocked.Exchange(ref _attached, 1) == 1)
            return;

        _workspace.WorkspaceRootChanged += OnWorkspaceRootChanged;

        void OnWorkspaceRootChanged(object? sender, WorkspaceChangedEventArgs e)
        {
            _workspace.WorkspaceRootChanged -= OnWorkspaceRootChanged;
            try
            {
                initializer.RunIfNeeded();
            }
            catch (Exception ex)
            {
                // 补初始化失败不应把切换本身判为失败：日志里能定位，后续页面会重试解析服务
                _logger.LogError(ex, "首次选定工作区后的补初始化失败");
            }
        }
    }
}
