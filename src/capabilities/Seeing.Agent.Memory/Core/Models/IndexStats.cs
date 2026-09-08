namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 索引统计信息
/// </summary>
public record IndexStats(
    int TotalDocuments,             // 总文档数
    int TotalVectors,               // 总向量数
    long IndexSizeBytes,            // 索引大小
    DateTimeOffset LastUpdated      // 最后更新时间
);
