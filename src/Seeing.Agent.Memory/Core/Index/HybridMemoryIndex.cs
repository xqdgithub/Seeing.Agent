using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;


namespace Seeing.Agent.Memory.Core.Index;

/// <summary>
/// 混合记忆索引 - 整合向量检索、关键词检索和 RRF 融合
/// </summary>
public class HybridMemoryIndex : IMemoryIndex
{
    private readonly IVectorIndex _vectorIndex;
    private readonly IKeywordIndex _keywordIndex;
    private readonly ILogger<HybridMemoryIndex>? _logger;

    /// <summary>
    /// 创建 HybridMemoryIndex 实例
    /// </summary>
    /// <param name="vectorIndex">向量索引</param>
    /// <param name="keywordIndex">关键词索引</param>
    /// <param name="logger">日志记录器</param>
    public HybridMemoryIndex(
        IVectorIndex vectorIndex,
        IKeywordIndex keywordIndex,
        ILogger<HybridMemoryIndex>? logger = null)
    {
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
        _keywordIndex = keywordIndex ?? throw new ArgumentNullException(nameof(keywordIndex));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task IndexAsync(FileNode node, CancellationToken ct = default)
    {
        await _vectorIndex.IndexAsync(node, ct);
        await _keywordIndex.IndexAsync(node, ct);
        _logger?.LogDebug("已索引文件: {Path}", node.Path);
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        var nodeList = nodes.ToList();
        await _vectorIndex.IndexBatchAsync(nodeList, ct);
        await _keywordIndex.IndexBatchAsync(nodeList, ct);
        _logger?.LogDebug("批量索引完成: {Count} 个文件", nodeList.Count);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        await _vectorIndex.RemoveAsync(path, ct);
        await _keywordIndex.RemoveAsync(path, ct);
        _logger?.LogDebug("已删除索引: {Path}", path);
    }

    /// <inheritdoc />
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        await _vectorIndex.ClearAsync(ct);
        await _keywordIndex.ClearAsync(ct);
        _logger?.LogDebug("已清空所有索引");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken ct = default)
    {
        var limit = query.Limit;

        switch (query.Mode)
        {
            case SearchMode.Vector:
                return await SearchVectorAsync(query.Text, limit, query.TypeFilter, ct);

            case SearchMode.Keyword:
                return await SearchKeywordAsync(query.Text, limit, ct);

            case SearchMode.Hybrid:
                return await SearchHybridAsync(query.Text, limit, query.VectorWeight, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(query.Mode), query.Mode, "不支持的检索模式");
        }
    }

    /// <inheritdoc />
    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        var vectorCount = await _vectorIndex.GetDocumentCountAsync(ct);
        var keywordCount = await _keywordIndex.GetDocumentCountAsync(ct);

        return new IndexStats(
            TotalDocuments: vectorCount + keywordCount,
            TotalVectors: vectorCount,
            IndexSizeBytes: 0,
            LastUpdated: DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// 向量检索
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> SearchVectorAsync(
        string query,
        int limit,
        string? typeFilter,
        CancellationToken ct)
    {
        var vectorResults = await _vectorIndex.SearchAsync(query, limit, ct);

        var hits = vectorResults.Select(r => new SearchHit(
            Node: CreatePlaceholderNode(r.Path),
            Score: r.Score,
            VectorScore: r.Score,
            KeywordScore: 0
        )).ToList();

        return hits;
    }

    /// <summary>
    /// 关键词检索
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> SearchKeywordAsync(
        string query,
        int limit,
        CancellationToken ct)
    {
        var keywordResults = await _keywordIndex.SearchAsync(query, limit, ct);

        var hits = keywordResults.Select(r => new SearchHit(
            Node: CreatePlaceholderNode(r.Path),
            Score: r.Score,
            VectorScore: 0,
            KeywordScore: r.Score
        )).ToList();

        return hits;
    }

    /// <summary>
    /// 混合检索 (RRF 融合)
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> SearchHybridAsync(
        string query,
        int limit,
        double vectorWeight,
        CancellationToken ct)
    {
        // 获取两种检索的结果
        var vectorResults = await _vectorIndex.SearchAsync(query, limit, ct);
        var keywordResults = await _keywordIndex.SearchAsync(query, limit, ct);

        // 转换为 RRF 输入格式
        var vectorInput = vectorResults.Select(r => (r.Path, r.Score)).ToList();
        var keywordInput = keywordResults.Select(r => (r.Path, r.Score)).ToList();

        // 执行 RRF 融合
        var fusedResults = RrfFusion.Fuse(vectorInput, keywordInput, vectorWeight);

        // 转换为 SearchHit
        var hits = fusedResults
            .Take(limit)
            .Select(r => new SearchHit(
                Node: CreatePlaceholderNode(r.Path),
                Score: r.Score,
                VectorScore: r.VectorScore,
                KeywordScore: r.KeywordScore
            ))
            .ToList();

        _logger?.LogDebug("混合检索 '{Query}' 融合完成，返回 {Count} 个结果", query, hits.Count);
        return hits;
    }

    /// <summary>
    /// 创建占位 FileNode（后续可从 FileStore 获取完整节点）
    /// </summary>
    private static FileNode CreatePlaceholderNode(string path)
    {
        return FileNode.Create(path, "", FileMetadata.Create(
            Guid.NewGuid().ToString("N")[..8],
            Models.MemoryType.Session,
            Path.GetFileNameWithoutExtension(path)
        ));
    }
}
