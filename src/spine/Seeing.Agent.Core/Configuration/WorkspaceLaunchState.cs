using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>进程内启动状态：工作区是否已选定并且项目装载已完成。</summary>
internal sealed class WorkspaceLaunchState : IWorkspaceLaunchState
{
    private volatile bool _ready;

    /// <inheritdoc/>
    public bool IsReady => _ready;

    /// <summary>标记工作区已就绪（首次选定并完成装载后调用）。</summary>
    public void MarkReady() => _ready = true;
}
