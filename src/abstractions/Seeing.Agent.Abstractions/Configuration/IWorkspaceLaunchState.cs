namespace Seeing.Agent.Configuration;

/// <summary>进程内「是否已有工作区」的只读事实，供托管服务决定能否启动。</summary>
public interface IWorkspaceLaunchState
{
    /// <summary>工作区是否已选定并且项目装载已经完成。</summary>
    bool IsReady { get; }
}
