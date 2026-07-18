namespace Seeing.Agent.Memory.Core.Graph;

/// <summary>
/// 图谱边类型
/// </summary>
public enum EdgeType
{
    /// <summary>引用 (Wikilink)</summary>
    Reference,
    
    /// <summary>父子关系 (目录层级)</summary>
    ParentChild,
    
    /// <summary>标签关联</summary>
    Tag,
    
    /// <summary>时间关联</summary>
    Temporal
}

/// <summary>
/// 图谱节点
/// </summary>
public record GraphNode(
    string Path,                    // 文件路径
    string Title,                   // 标题
    int Degree                      // 度数（连接数）
);

/// <summary>
/// 图谱边
/// </summary>
public record GraphEdge(
    string SourcePath,              // 源节点路径
    string TargetPath,              // 目标节点路径
    EdgeType Type,                  // 边类型
    double Weight = 1.0,            // 权重
    string? Context = null          // 上下文（如引用处的内容）
);

/// <summary>
/// 图谱查询结果
/// </summary>
public record GraphQueryResult(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges
);

/// <summary>
/// 知识图谱接口 - 基于 Wikilink 的文件关联图
/// </summary>
public interface IMemoryGraph
{
    /// <summary>添加节点</summary>
    Task AddNodeAsync(string path, string title, CancellationToken ct = default);

    /// <summary>添加边</summary>
    Task AddEdgeAsync(string sourcePath, string targetPath, EdgeType type, 
        double weight = 1.0, string? context = null, CancellationToken ct = default);

    /// <summary>移除节点及其所有边</summary>
    Task RemoveNodeAsync(string path, CancellationToken ct = default);

    /// <summary>移除指定边</summary>
    Task RemoveEdgeAsync(string sourcePath, string targetPath, EdgeType type, CancellationToken ct = default);

    /// <summary>获取邻居节点</summary>
    Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(string path, int depth = 1, CancellationToken ct = default);

    /// <summary>获取两个节点间的路径</summary>
    Task<IReadOnlyList<string>> FindPathAsync(string sourcePath, string targetPath, 
        int maxDepth = 5, CancellationToken ct = default);

    /// <summary>获取图统计</summary>
    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>查询图谱</summary>
    Task<GraphQueryResult> QueryAsync(string? startPath = null, int depth = 1, 
        CancellationToken ct = default);
}

/// <summary>
/// 图统计信息
/// </summary>
public record GraphStats(
    int NodeCount,
    int EdgeCount,
    int IsolatedNodes
);
