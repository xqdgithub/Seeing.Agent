namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动控制端口：按执行身份取消其派生的后台活动（子会话、后台任务等）。
/// <para>
/// 该端口为**可选依赖**：宿主未注册实现时，调用方必须跳过取消而不是抛异常。
/// </para>
/// </summary>
public interface IBackgroundActivityControl
{
    /// <summary>取消归属于指定执行的全部后台活动，返回被取消的条目数。</summary>
    Task<int> CancelByExecutionAsync(
        string executionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default);
}

/// <summary>后台活动取消原因（用于审计与差异化处理）。</summary>
public enum BackgroundActivityCancelReason
{
    /// <summary>用户显式中断当前轮。</summary>
    User,

    /// <summary>会话被停止。</summary>
    SessionStopped,

    /// <summary>由父级活动级联取消。</summary>
    CascadeFromParent,

    /// <summary>宿主关闭。</summary>
    HostShutdown,

    /// <summary>工作区切换。</summary>
    WorkspaceSwitched,

    /// <summary>所属模块被停用。</summary>
    ModuleDeactivated,

    /// <summary>超时。</summary>
    Timeout,

    /// <summary>被新一轮执行取代。</summary>
    Superseded
}
