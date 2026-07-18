using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆统一服务实现
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly IMemoryGraph _graph;
    private readonly ILogger<MemoryService>? _logger;

    public MemoryService(
        IFileStore fileStore,
        IMemoryIndex index,
        IMemoryGraph graph,
        ILogger<MemoryService>? logger = null)
    {
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileNode> SaveAsync(string path, string content, CancellationToken ct = default)
    {
        // 写入文件
        var node = await _fileStore.WriteAsync(path, content, ct);

        // 更新索引
        await _index.IndexAsync(node, ct);

        // 更新图谱（解析 Wikilinks）
        await UpdateGraphAsync(node, ct);

        _logger?.LogDebug("已保存记忆: {Path}", path);
        return node;
    }

    /// <inheritdoc />
    public async Task<FileNode?> GetAsync(string path, CancellationToken ct = default)
    {
        return await _fileStore.ReadAsync(path, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        // 删除文件
        await _fileStore.DeleteAsync(path, ct);

        // 删除索引
        await _index.RemoveAsync(path, ct);

        // 删除图谱节点
        await _graph.RemoveNodeAsync(path, ct);

        _logger?.LogDebug("已删除记忆: {Path}", path);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default)
    {
        return await _fileStore.ListAsync(pattern, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query,
        CancellationToken ct = default)
    {
        return await _index.SearchAsync(query, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetRelatedAsync(string path, int depth = 1, CancellationToken ct = default)
    {
        return await _graph.GetNeighborsAsync(path, depth, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindPathAsync(string source, string target, CancellationToken ct = default)
    {
        return await _graph.FindPathAsync(source, target, ct: ct);
    }

    /// <inheritdoc />
    public async Task<MemoryStats> GetStatsAsync(CancellationToken ct = default)
    {
        var files = await _fileStore.ListAsync(ct: ct);
        var indexStats = await _index.GetStatsAsync(ct);
        var graphStats = await _graph.GetStatsAsync(ct);

        return new MemoryStats(
            FileCount: files.Count,
            Index: indexStats,
            Graph: graphStats
        );
    }

    /// <summary>
    /// 更新知识图谱
    /// </summary>
    private async Task UpdateGraphAsync(FileNode node, CancellationToken ct)
    {
        // 添加当前节点
        await _graph.AddNodeAsync(node.Path, node.Metadata.Title ?? node.Path, ct);

        // 添加 Wikilink 引用边
        foreach (var link in node.Links)
        {
            // 尝试解析链接目标路径
            var targetPath = ResolveLinkPath(link);
            if (targetPath != null)
            {
                await _graph.AddEdgeAsync(node.Path, targetPath, EdgeType.Reference, ct: ct);
            }
        }

        // 添加父子关系（基于目录层级）
        var dir = Path.GetDirectoryName(node.Path)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir) && dir != ".")
        {
            await _graph.AddEdgeAsync(dir, node.Path, EdgeType.ParentChild, ct: ct);
        }

        // 添加标签关联
        foreach (var tag in node.Metadata.Tags)
        {
            var tagPath = $"tag/{tag}";
            await _graph.AddNodeAsync(tagPath, $"#{tag}", ct);
            await _graph.AddEdgeAsync(node.Path, tagPath, EdgeType.Tag, ct: ct);
        }
    }

    /// <summary>
    /// 解析链接路径
    /// </summary>
    private static string? ResolveLinkPath(string link)
    {
        // 简单的路径解析：如果链接包含 / 或 .md，视为完整路径
        // 否则尝试在 daily/ 或 session/ 目录下查找
        if (link.Contains('/') || link.EndsWith(".md"))
        {
            return link;
        }

        // 尝试常见的路径模式
        var possiblePaths = new[]
        {
            $"daily/{link}.md",
            $"session/{link}.md",
            $"digest/{link}.md"
        };

        // 这里应该检查文件是否存在，但为了简化先返回第一个可能的路径
        return possiblePaths[0];
    }
}
