namespace Seeing.Agent.Memory.Configuration;

/// <summary>
/// MemoryOrchestrator 配置选项。
/// 控制 Hybrid 写入调度策略。
/// </summary>
public class MemoryOrchestratorOptions
{
    /// <summary>
    /// 立即写入的重要性阈值。
    /// 重要性 >= 此阈值的记忆将立即写入（Hot Path）。
    /// 默认 0.8。
    /// </summary>
    public double ImmediateWriteThreshold { get; set; } = 0.8;

    /// <summary>
    /// 批量合并队列最大容量。
    /// 队列达到此容量时触发强制合并。
    /// 默认 100。
    /// </summary>
    public int ConsolidationQueueCapacity { get; set; } = 100;

    /// <summary>
    /// 批量合并执行间隔（秒）。
    /// 默认 60 秒。
    /// </summary>
    public int ConsolidationIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 去重相似度阈值。
    /// 默认 0.8。
    /// </summary>
    public double DeduplicationThreshold { get; set; } = 0.8;

    /// <summary>
    /// 是否在合并时创建摘要记忆。
    /// 默认 false（ADD-only 策略）。
    /// </summary>
    public bool CreateSummaryOnConsolidation { get; set; } = false;

    /// <summary>
    /// 批量合并时最小记忆数量。
    /// 少于此数量的会话不执行合并。
    /// 默认 3。
    /// </summary>
    public int MinMemoriesForConsolidation { get; set; } = 3;
}