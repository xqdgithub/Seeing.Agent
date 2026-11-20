namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 子进程生命周期归属 — 声明「谁负责回收这个进程」。
/// </summary>
public enum SubprocessLifecycle
{
    /// <summary>
    /// 默认：随宿主进程存亡。宿主退出（含崩溃）时，进程树由内核回收；
    /// 启动器（如 <c>bash -c "npm run dev &amp;"</c>）退出不影响后台子进程。
    /// </summary>
    HostBound = 0,

    /// <summary>
    /// 脱离宿主：宿主退出后进程仍然存活，由调用方自行负责回收。
    /// </summary>
    Detached = 1,
}
