namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动会话面——能力包工具只注入此接口。
/// <para>
/// 归属语义（越权行为逐方法定死）：
/// <list type="bullet">
/// <item><see cref="StartAsync"/>：<c>OwnerSessionId != requesterSessionId</c> → 抛 <see cref="UnauthorizedAccessException"/>。</item>
/// <item><see cref="Get"/>：越权返回 <c>null</c>；仅当 <c>Visibility ∈ {Project, System}</c> 时放行。</item>
/// <item><see cref="List"/>：只返回 <c>OwnerSessionId == requesterSessionId</c> 的活动。</item>
/// <item><see cref="CancelAsync"/>：越权返回 <c>false</c>。</item>
/// <item><see cref="WaitAsync"/>：越权返回 <see cref="BackgroundActivityWaitOutcome.TimedOut"/>。</item>
/// <item><see cref="ReadOutputAsync"/>：越权返回空页（<c>EndOfStream = true</c>）。</item>
/// <item><see cref="MarkResultObserved"/>：越权忽略。</item>
/// </list>
/// </para>
/// <para>
/// 系统级操作（登记 launcher、跨会话取消、系统级启动、孤儿收敛）不在本接口上，见
/// <see cref="IBackgroundActivityControl"/>。
/// </para>
/// </summary>
public interface IBackgroundActivityManager
{
    /// <summary>
    /// 启动后台活动并立即返回句柄。
    /// <para><paramref name="requesterSessionId"/> 即归属会话；与 <c>request.OwnerSessionId</c> 不等时抛 <see cref="UnauthorizedAccessException"/>。</para>
    /// </summary>
    Task<BackgroundActivityHandle> StartAsync(
        BackgroundActivityStartRequest request,
        string requesterSessionId,
        CancellationToken ct = default);

    /// <summary>查询单个活动；越权或不存在返回 <c>null</c>。</summary>
    BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId);

    /// <summary>列出归属本会话的活动（UI 首屏重建与事件回放的唯一数据来源）。</summary>
    IReadOnlyList<BackgroundActivitySnapshot> List(BackgroundActivityQuery query, string requesterSessionId);

    /// <summary>取消活动及其子树；越权返回 <c>false</c>。</summary>
    Task<bool> CancelAsync(
        string activityId,
        BackgroundActivityCancelReason reason,
        string requesterSessionId,
        CancellationToken ct = default);

    /// <summary>等待活动到达终态或超时；调用方取消返回 <see cref="BackgroundActivityWaitOutcome.CallerCancelled"/>，不取消活动本身。</summary>
    Task<BackgroundActivityWaitResult> WaitAsync(
        string activityId,
        TimeSpan timeout,
        string requesterSessionId,
        CancellationToken ct = default);

    /// <summary>分页读取活动输出；越权返回空页。</summary>
    Task<BackgroundActivityOutputPage> ReadOutputAsync(
        string activityId,
        long offset,
        int maxBytes,
        string requesterSessionId,
        CancellationToken ct = default);

    /// <summary>标记结果已被发起方观察（此后不再投递完成唤醒）；越权忽略。</summary>
    void MarkResultObserved(string activityId, string requesterSessionId);
}
