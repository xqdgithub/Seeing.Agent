using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 将会话缓冲 flush 到工作队列或同步走 Pipeline（带每分钟次数限流）。
/// </summary>
public interface IMemoryFlushService
{
    /// <summary>尝试将 batch 入队；限流失败时把内容还回缓冲并返回 false。</summary>
    bool TryEnqueueBatch(MemoryBatch batch);

    /// <summary>取出会话全部缓冲并入队（若有）。</summary>
    bool TryFlushSession(string sessionId);

    /// <summary>取出缓冲并同步走 Pipeline（会话结束 / Evolution 前使用）。</summary>
    Task FlushSessionInlineAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Agent 一轮完成后：可能产生 batch 并入队。</summary>
    bool TryFlushAfterTurn(string sessionId);

    /// <summary>按空闲阈值 flush 所有到期会话（入队）。</summary>
    int FlushIdleSessions();
}
