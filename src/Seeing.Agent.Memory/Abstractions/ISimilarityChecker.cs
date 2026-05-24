using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 相似度检查器接口。
/// 用于计算记忆条目之间的相似度，支持后期扩展向量相似度。
/// </summary>
public interface ISimilarityChecker
{
    /// <summary>
    /// 计算两个记忆条目之间的相似度。
    /// </summary>
    /// <param name="entry1">第一个记忆条目</param>
    /// <param name="entry2">第二个记忆条目</param>
    /// <returns>相似度分数，范围 [0.0, 1.0]，1.0 表示完全相同</returns>
    Task<double> CalculateSimilarityAsync(MemoryEntry entry1, MemoryEntry entry2);

    /// <summary>
    /// 判断两个记忆条目是否为重复。
    /// </summary>
    /// <param name="entry1">第一个记忆条目</param>
    /// <param name="entry2">第二个记忆条目</param>
    /// <param name="threshold">相似度阈值，默认 0.8</param>
    /// <returns>如果相似度大于等于阈值，返回 true</returns>
    Task<bool> IsDuplicateAsync(MemoryEntry entry1, MemoryEntry entry2, double threshold = 0.8);
}

/// <summary>
/// 相似度检查器类型标识。
/// </summary>
public enum SimilarityCheckerType
{
    /// <summary>
    /// 文本相似度（关键词匹配）
    /// </summary>
    Text,

    /// <summary>
    /// 向量相似度（嵌入向量）
    /// </summary>
    Vector,

    /// <summary>
    /// 混合相似度（文本 + 向量）
    /// </summary>
    Hybrid
}