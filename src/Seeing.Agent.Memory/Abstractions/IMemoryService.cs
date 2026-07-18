using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 记忆统一服务接口
/// </summary>
public interface IMemoryService
{
    // ===== 文件操作 =====
    
    /// <summary>保存记忆文件</summary>
    Task<FileNode> SaveAsync(string path, string content, CancellationToken ct = default);
    
    /// <summary>读取记忆文件</summary>
    Task<FileNode?> GetAsync(string path, CancellationToken ct = default);
    
    /// <summary>删除记忆文件</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);
    
    /// <summary>列出所有记忆文件</summary>
    Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default);

    // ===== 检索操作 =====
    
    /// <summary>搜索记忆</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken ct = default);

    // ===== 图谱操作 =====
    
    /// <summary>获取关联记忆</summary>
    Task<IReadOnlyList<GraphNode>> GetRelatedAsync(string path, int depth = 1, CancellationToken ct = default);
    
    /// <summary>查找记忆路径</summary>
    Task<IReadOnlyList<string>> FindPathAsync(string source, string target, CancellationToken ct = default);

    // ===== 统计 =====
    
    /// <summary>获取统计信息</summary>
    Task<MemoryStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// 记忆统计信息
/// </summary>
public record MemoryStats(
    int FileCount,
    IndexStats Index,
    GraphStats Graph
);
