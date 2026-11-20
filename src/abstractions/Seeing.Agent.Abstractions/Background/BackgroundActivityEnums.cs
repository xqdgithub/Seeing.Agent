namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动状态。
/// <para>
/// 终态（<see cref="Succeeded"/> / <see cref="Failed"/> / <see cref="Cancelled"/> / <see cref="Interrupted"/>）
/// 不可逆，只能由 manager 的 CAS 进入一次。
/// </para>
/// </summary>
public enum BackgroundActivityStatus
{
    /// <summary>已登记但尚未开始执行。</summary>
    Pending,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>正常完成。</summary>
    Succeeded,

    /// <summary>失败（启动异常 / 执行异常 / 未上报终态）。</summary>
    Failed,

    /// <summary>被取消（用户、级联、会话停止、超时等）。</summary>
    Cancelled,

    /// <summary>被中断（宿主关闭、孤儿收敛），不可再续。</summary>
    Interrupted
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

/// <summary>活动进入终态时的完成通知策略。</summary>
public enum BackgroundActivityNotifyPolicy
{
    /// <summary>终态时写入 outbox，唤醒发起会话。</summary>
    OnComplete,

    /// <summary>不通知。</summary>
    None
}

/// <summary>活动对用户的可见范围（决定跨会话查询是否放行）。</summary>
public enum BackgroundActivityVisibility
{
    /// <summary>仅归属会话可见。</summary>
    Session,

    /// <summary>项目内可见。</summary>
    Project,

    /// <summary>系统级（不对用户呈现）。</summary>
    System
}

/// <summary>活动发起来源。</summary>
public enum BackgroundActivityOrigin
{
    /// <summary>工具调用转后台（如 <c>bash background=true</c>）。</summary>
    ToolBackground,

    /// <summary>用户显式发起。</summary>
    User,

    /// <summary>调度器旁路执行。</summary>
    Scheduler,

    /// <summary>模块自管的后台运行。</summary>
    Module
}

/// <summary>活动生命周期边界。</summary>
public enum BackgroundActivityLifetime
{
    /// <summary>随宿主进程存活。</summary>
    Host,

    /// <summary>脱离宿主进程存活（预留，一期不实现）。</summary>
    Detached
}

/// <summary>终态结果的引用类型（结果内容不内联进活动记录）。</summary>
public enum BackgroundActivityResultKind
{
    /// <summary>无结果引用。</summary>
    None,

    /// <summary>文本结果（经输出存储读取）。</summary>
    Text,

    /// <summary>产物文件。</summary>
    Artifact,

    /// <summary>关联会话。</summary>
    Session,

    /// <summary>进程退出码。</summary>
    ProcessExit
}

/// <summary>同一 <c>DedupeKey</c> 重复启动时的处理策略。</summary>
public enum BackgroundActivityDedupeStrategy
{
    /// <summary>拒绝新的启动（默认）。</summary>
    Reject,

    /// <summary>取消旧活动并以新的取代。</summary>
    Supersede
}

/// <summary>等待活动的结束方式。</summary>
public enum BackgroundActivityWaitOutcome
{
    /// <summary>已在期限内到达终态。</summary>
    Completed,

    /// <summary>等待超时，活动仍在运行。</summary>
    TimedOut,

    /// <summary>调用方取消（不取消活动本身）。</summary>
    CallerCancelled
}

/// <summary>
/// 活动能力位——唯一可驱动框架行为的活动属性。
/// <para>框架任何分支不得按 <c>Kind</c> 分支或读取 <c>Attributes</c>。</para>
/// </summary>
[Flags]
public enum BackgroundActivityCapabilities
{
    /// <summary>无能力。</summary>
    None = 0,

    /// <summary>可取消。</summary>
    CanCancel = 1 << 0,

    /// <summary>可等待。</summary>
    CanWait = 1 << 1,

    /// <summary>可流式读取输出。</summary>
    CanStreamOutput = 1 << 2,

    /// <summary>可附加到会话视图。</summary>
    CanAttach = 1 << 3,

    /// <summary>完成时会产生明确信号（可投递唤醒）。</summary>
    SignalsCompletion = 1 << 4
}
