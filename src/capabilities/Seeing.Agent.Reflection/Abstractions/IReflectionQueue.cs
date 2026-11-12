using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

/// <summary>
/// 入队结果。
/// <para>
/// 用枚举而不是 <c>bool</c>：<c>false</c> 同时表示「同会话已存在，只更新了内容」
/// 与「队列满，本次丢弃」——两者对调用方的含义完全相反（前者是成功去重，
/// 后者是失败丢弃），日志和指标必须能区分。
/// </para>
/// </summary>
public enum ReflectionEnqueueResult
{
    /// <summary>新会话入队成功。</summary>
    Enqueued = 0,

    /// <summary>
    /// 该会话已在队列中：请求内容已覆盖为最新，排队位置保持不变。
    /// <para>这是正常的去重行为，不是失败。</para>
    /// </summary>
    Updated = 1,

    /// <summary>队列已满，本次请求被丢弃。</summary>
    Dropped = 2,

    /// <summary>请求无效（会话 ID 为空），未入队。</summary>
    Invalid = 3
}

/// <summary>
/// 复盘请求队列：按会话去重的进程内有界队列。
/// <para>
/// 去重语义：同一 <see cref="ReflectionRequest.SessionId"/> 在队列中最多一条；
/// 重复入队只更新该会话的请求内容（保留原排队位置），避免同一会话被连续复盘。
/// </para>
/// </summary>
public interface IReflectionQueue
{
    /// <summary>
    /// 尝试入队（非阻塞）。
    /// </summary>
    /// <param name="request">复盘请求。</param>
    /// <returns>入队结果，区分「新增 / 已存在已更新 / 队列满丢弃 / 请求无效」。</returns>
    ReflectionEnqueueResult TryEnqueue(ReflectionRequest request);

    /// <summary>非阻塞取出一条请求（供 Worker 单循环排空）。</summary>
    /// <param name="request">取出的请求（以队列内最新内容为准）。</param>
    /// <returns>队列非空且成功取出时为 <c>true</c>。</returns>
    bool TryDequeue(out ReflectionRequest request);

    /// <summary>阻塞等待一条请求（供需要异步等待的消费方使用）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ReflectionRequest> DequeueAsync(CancellationToken ct);

    /// <summary>当前排队中的会话数。</summary>
    int PendingCount { get; }

    /// <summary>
    /// 当前排队请求的快照（供设置卡/记录页展示「在复盘中」）。
    /// <para>
    /// 只读快照，不与消费竞争：排空仍走 <see cref="TryDequeue"/> / <see cref="DequeueAsync"/>。
    /// 直接暴露集合会让调用方误以为可以移除条目（那会破坏「字典有值 ⇔ 通道有槽」的不变量）。
    /// </para>
    /// </summary>
    IReadOnlyList<ReflectionRequest> Snapshot();
}
