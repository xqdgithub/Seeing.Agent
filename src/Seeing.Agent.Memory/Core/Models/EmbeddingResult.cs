namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// Embedding 结果
/// </summary>
public record EmbeddingResult(
    string Text,                    // 原始文本
    float[] Vector,                 // 向量
    int TokenCount                  // Token 数量
);
