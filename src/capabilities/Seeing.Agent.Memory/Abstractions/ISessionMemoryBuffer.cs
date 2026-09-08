using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 按会话缓冲 Chat 候选；轮次到达阈值或外部请求时取出批量。
/// </summary>
public interface ISessionMemoryBuffer
{
    /// <summary>加入候选；若因溢出需要强制 flush，返回待处理 batch（调用方负责入队）。</summary>
    MemoryBatch? Add(MemoryCandidate candidate);

    /// <summary>
    /// 记录一次顶层 Agent 完成。若达到 ExtractEveryNTurns 且有缓冲，取出并重置轮次。
    /// </summary>
    MemoryBatch? OnAgentTurnCompleted(string sessionId);

    /// <summary>取出并清空指定会话缓冲（会话结束 / 主动 flush）。</summary>
    MemoryBatch? TakeAll(string sessionId);

    /// <summary>取出所有空闲超过 <paramref name="idle"/> 且仍有内容的会话缓冲。</summary>
    IReadOnlyList<MemoryBatch> TakeIdleBatches(TimeSpan idle);

    /// <summary>限流/队列满时把已取出的候选还回缓冲（不触发溢出 flush）。</summary>
    void Requeue(string sessionId, IReadOnlyList<MemoryCandidate> candidates);

    int GetPendingCount(string sessionId);
    int GetTurnCount(string sessionId);

    /// <summary>限流导致 flush 失败时恢复轮次进度，便于下一轮重试。</summary>
    void SetTurnCount(string sessionId, int turns);
}
