using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 混合检索索引接口
/// </summary>
public interface IMemoryIndex
{
    // ===== 索引操作 =====
    
    /// <summary>索引单个文件</summary>
    Task IndexAsync(FileNode node, CancellationToken ct = default);
    
    /// <summary>批量索引</summary>
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    
    /// <summary>移除索引</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);
    
    /// <summary>重建所有索引</summary>
    Task RebuildAsync(CancellationToken ct = default);
    
    // ===== 检索操作 =====
    
    /// <summary>搜索</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken ct = default);
    
    // ===== 统计 =====
    
    /// <summary>获取索引统计</summary>
    Task<IndexStats> GetStatsAsync(CancellationToken ct = default);
}
