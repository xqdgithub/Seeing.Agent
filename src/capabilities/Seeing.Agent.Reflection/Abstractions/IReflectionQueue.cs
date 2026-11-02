using Seeing.Agent.Reflection.Core;

namespace Seeing.Agent.Reflection.Abstractions;

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
    /// <returns>
    /// <c>true</c> 表示新增成功；<c>false</c> 表示该会话已在队列中（请求内容已覆盖）
    /// 或队列已满（本次丢弃）。
    /// </returns>
    bool TryEnqueue(ReflectionRequest request);

    /// <summary>非阻塞取出一条请求（供 Worker 单循环排空）。</summary>
    /// <param name="request">取出的请求（以队列内最新内容为准）。</param>
    /// <returns>队列非空且成功取出时为 <c>true</c>。</returns>
    bool TryDequeue(out ReflectionRequest request);

    /// <summary>阻塞等待一条请求（供需要异步等待的消费方使用）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ReflectionRequest> DequeueAsync(CancellationToken ct);

    /// <summary>当前排队中的会话数。</summary>
    int PendingCount { get; }
}
