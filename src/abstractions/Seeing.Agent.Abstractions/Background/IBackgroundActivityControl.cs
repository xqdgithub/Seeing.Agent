namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动系统面——承担一切"可能作用于他人会话"或"改变全局登记"的操作。
/// <para>
/// 只注册给 Core / 宿主与模块激活器；能力包工具（含 <c>bash</c> 与 <c>activity_*</c>）不得注入本接口。
/// </para>
/// <para>
/// 能力包（含模块激活器）按**可选依赖**处理：宿主未注册实现时跳过而不是抛异常
/// （如 Shell 模块按需登记 bash 后台 launcher）。
/// </para>
/// <para>
/// <c>AddSeeingCore</c> 组合根总是注册本端口（manager 与 control 同一实例），
/// 因此执行世界（<c>SessionRuntime</c> 的级联取消）把它当作**必需依赖**解析。
/// </para>
/// </summary>
public interface IBackgroundActivityControl
{
    /// <summary>系统级启动：可以指定任意 <c>OwnerSessionId</c>。</summary>
    Task<BackgroundActivityHandle> StartAsSystemAsync(
        BackgroundActivityStartRequest request,
        CancellationToken ct = default);

    /// <summary>登记一条由外部机制启动的活动（宿主提供完成信号或自行上报终态）。</summary>
    Task<BackgroundActivityHandle> TrackAsync(
        BackgroundActivityTrackRequest request,
        CancellationToken ct = default);

    /// <summary>按模块登记 launcher（kind 由 launcher 自身声明）。</summary>
    void RegisterLauncher(string moduleId, IBackgroundActivityLauncher launcher);

    /// <summary>
    /// 注销某 kind 的 launcher，返回取消的活动数。
    /// <para>返回前必须取消该 kind 名下全部运行中活动并等待其终态。</para>
    /// </summary>
    Task<int> UnregisterLauncherAsync(string kind, CancellationToken ct = default);

    /// <summary>按会话取消（覆盖其全部活动），返回被取消的条目数。</summary>
    Task<int> CancelBySessionAsync(
        string sessionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default);

    /// <summary>按执行取消（精确到本轮执行），返回被取消的条目数。</summary>
    Task<int> CancelByExecutionAsync(
        string executionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default);

    /// <summary>取消全部活动（工作区切换、宿主关闭），返回被取消的条目数。</summary>
    Task<int> CancelAllAsync(
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default);

    /// <summary>
    /// 收敛孤儿活动（宿主已死仍为 <c>Running</c>）：只落记录为 <c>Interrupted</c>，不入 outbox、不发事件。
    /// </summary>
    Task<int> ReconcileOrphanedActivitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// 回收终态且 <see cref="BackgroundActivitySnapshot.CompletedAt"/> 早于 <paramref name="cutoff"/> 的活动：
    /// 从内存表移除，并删除其输出文件与落盘快照。
    /// <para>
    /// <b>只删终态</b>：<c>Running</c> / <c>Pending</c> 永远保留（删掉在跑的活动等于丢掉唯一的控制入口）。
    /// </para>
    /// <para>单个活动删除失败只记日志并保留记录，下次清理重试；返回实际移除条数。</para>
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
