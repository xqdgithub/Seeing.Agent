namespace Seeing.Agent.Abstractions.Compression;

/// <summary>
/// 压缩编排入口 — TokenBudget 只决策、实现方执行。
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// 执行压缩：摘要 → 写回历史 → 持久化
    /// </summary>
    Task<CompressionOutcome> CompressAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default);
}
