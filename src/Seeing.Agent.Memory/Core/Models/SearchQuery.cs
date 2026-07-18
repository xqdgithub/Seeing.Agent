namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 搜索查询
/// </summary>
public record SearchQuery(
    string Text,                    // 查询文本
    SearchMode Mode,                // 检索模式
    int Limit = 10,                 // 返回数量
    double VectorWeight = 0.5,      // 向量权重 (Hybrid 模式)
    string? TypeFilter = null,      // 类型过滤
    IReadOnlyList<string>? TagFilter = null  // 标签过滤
);

/// <summary>
/// 检索模式
/// </summary>
public enum SearchMode
{
    /// <summary>向量语义检索</summary>
    Vector,
    
    /// <summary>关键词检索 (BM25)</summary>
    Keyword,
    
    /// <summary>混合检索 (RRF 融合)</summary>
    Hybrid
}
