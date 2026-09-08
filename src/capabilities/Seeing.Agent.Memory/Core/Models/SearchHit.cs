namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 搜索结果
/// </summary>
public record SearchHit(
    FileNode Node,                  // 文件节点
    double Score,                   // 综合得分
    double VectorScore,             // 向量得分
    double KeywordScore,            // 关键词得分
    string? Highlight = null        // 高亮片段
);
