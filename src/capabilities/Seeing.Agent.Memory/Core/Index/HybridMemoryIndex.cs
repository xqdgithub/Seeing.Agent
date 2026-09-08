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
    private readonly IEmbeddingStatus _embeddingStatus;
    private readonly IFileStore _fileStore;
    private readonly ILogger<HybridMemoryIndex>? _logger;

    public HybridMemoryIndex(
        IVectorIndex vectorIndex,
        IKeywordIndex keywordIndex,
        IEmbeddingStatus embeddingStatus,
        IFileStore fileStore,
        ILogger<HybridMemoryIndex>? logger = null)
    {
        _vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
        _keywordIndex = keywordIndex ?? throw new ArgumentNullException(nameof(keywordIndex));
        _embeddingStatus = embeddingStatus ?? throw new ArgumentNullException(nameof(embeddingStatus));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _logger = logger;
    }

    public async Task IndexAsync(FileNode node, CancellationToken ct = default)
    {
        if (_embeddingStatus.IsAvailable)
            await _vectorIndex.IndexAsync(node, ct);
        await _keywordIndex.IndexAsync(node, ct);
        _logger?.LogDebug("已索引文件: {Path}", node.Path);
    }

    public async Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        var nodeList = nodes.ToList();
        if (_embeddingStatus.IsAvailable)
            await _vectorIndex.IndexBatchAsync(nodeList, ct);
        await _keywordIndex.IndexBatchAsync(nodeList, ct);
        _logger?.LogDebug("批量索引完成: {Count} 个文件", nodeList.Count);
    }

    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        if (_embeddingStatus.IsAvailable)
            await _vectorIndex.RemoveAsync(path, ct);
        await _keywordIndex.RemoveAsync(path, ct);
        _logger?.LogDebug("已删除索引: {Path}", path);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (_embeddingStatus.IsAvailable)
            await _vectorIndex.ClearAsync(ct);
        await _keywordIndex.ClearAsync(ct);
        _logger?.LogInformation("已清空全部索引");
    }

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        await ClearAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken ct = default)
    {
        var limit = query.Limit;

        if (query.Mode is SearchMode.Vector or SearchMode.Hybrid && !_embeddingStatus.IsAvailable)
            return await SearchKeywordAsync(query.Text, limit, ct);

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

    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        var vectorCount = _embeddingStatus.IsAvailable
            ? await _vectorIndex.GetDocumentCountAsync(ct)
            : 0;
        var keywordCount = await _keywordIndex.GetDocumentCountAsync(ct);

        return new IndexStats(
            TotalDocuments: vectorCount + keywordCount,
            TotalVectors: vectorCount,
            IndexSizeBytes: 0,
            LastUpdated: DateTimeOffset.UtcNow
        );
    }

    private async Task<IReadOnlyList<SearchHit>> SearchVectorAsync(
        string query,
        int limit,
        string? typeFilter,
        CancellationToken ct)
    {
        var vectorResults = await _vectorIndex.SearchAsync(query, limit, ct);
        var hits = new List<SearchHit>();
        foreach (var r in vectorResults)
        {
            hits.Add(new SearchHit(
                Node: await ResolveNodeAsync(r.Path, ct),
                Score: r.Score,
                VectorScore: r.Score,
                KeywordScore: 0));
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchKeywordAsync(
        string query,
        int limit,
        CancellationToken ct)
    {
        var keywordResults = await _keywordIndex.SearchAsync(query, limit, ct);
        var hits = new List<SearchHit>();
        foreach (var r in keywordResults)
        {
            hits.Add(new SearchHit(
                Node: await ResolveNodeAsync(r.Path, ct),
                Score: r.Score,
                VectorScore: 0,
                KeywordScore: r.Score));
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchHybridAsync(
        string query,
        int limit,
        double vectorWeight,
        CancellationToken ct)
    {
        var vectorResults = await _vectorIndex.SearchAsync(query, limit, ct);
        var keywordResults = await _keywordIndex.SearchAsync(query, limit, ct);

        var vectorInput = vectorResults.Select(r => (r.Path, r.Score)).ToList();
        var keywordInput = keywordResults.Select(r => (r.Path, r.Score)).ToList();

        var fusedResults = RrfFusion.Fuse(vectorInput, keywordInput, vectorWeight);

        var hits = new List<SearchHit>();
        foreach (var r in fusedResults.Take(limit))
        {
            hits.Add(new SearchHit(
                Node: await ResolveNodeAsync(r.Path, ct),
                Score: r.Score,
                VectorScore: r.VectorScore,
                KeywordScore: r.KeywordScore));
        }

        _logger?.LogDebug("混合检索 '{Query}' 融合完成，返回 {Count} 个结果", query, hits.Count);
        return hits;
    }

    private async Task<FileNode> ResolveNodeAsync(string path, CancellationToken ct)
    {
        try
        {
            var node = await _fileStore.ReadAsync(path, ct);
            if (node != null)
                return node;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "回填记忆节点失败: {Path}", path);
        }

        return CreatePlaceholderNode(path);
    }

    private static FileNode CreatePlaceholderNode(string path)
    {
        return FileNode.Create(path, "", FileMetadata.Create(
            Guid.NewGuid().ToString("N")[..8],
            Models.MemoryType.Session,
            Path.GetFileNameWithoutExtension(path)
        ));
    }
}
