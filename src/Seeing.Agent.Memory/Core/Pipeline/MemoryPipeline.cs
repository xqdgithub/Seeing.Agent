using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Storage;

namespace Seeing.Agent.Memory.Core.Pipeline;

public sealed class MemoryPipeline : IMemoryPipeline
{
    private readonly IMemoryHeuristicFilter _filter;
    private readonly IMemoryExtractor _extractor;
    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly IMemoryGraph _graph;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<MemoryPipeline>? _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public MemoryPipeline(
        IMemoryHeuristicFilter filter,
        IMemoryExtractor extractor,
        IFileStore fileStore,
        IMemoryIndex index,
        IMemoryGraph graph,
        IOptions<MemoryOptions> options,
        ILogger<MemoryPipeline>? logger = null)
    {
        _filter = filter;
        _extractor = extractor;
        _fileStore = fileStore;
        _index = index;
        _graph = graph;
        _options = options;
        _logger = logger;
    }

    public async Task<PipelineResult> ProcessAsync(MemoryCandidate candidate, CancellationToken ct = default)
    {
        if (!_options.Value.Enabled)
            return new PipelineResult(false, null, "disabled");

        var decision = _filter.Evaluate(candidate);
        if (!decision.Accepted)
            return new PipelineResult(false, null, decision.Reason);

        if (!_options.Value.Extraction.Enabled)
            return new PipelineResult(false, null, "extraction_disabled");

        var extraction = await _extractor.ExtractAsync(candidate, ct);
        if (extraction is null)
            return new PipelineResult(false, null, "extract_skipped");

        var id = candidate.Id;
        var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var dailyPath = $"daily/{date}/{id}.md";
        var tagsYaml = string.Join(", ", extraction.Tags.Select(t => t));
        var dailyContent = $"""
            ---
            id: {id}
            type: daily
            title: "{EscapeYaml(extraction.Title)}"
            tags: [{tagsYaml}]
            importance: {extraction.Importance:0.###}
            kind: {extraction.Kind}
            source_session: {candidate.SessionId}
            created_at: {DateTimeOffset.UtcNow:O}
            ---

            {extraction.Content}
            """;

        var dailyNode = await _fileStore.WriteAsync(dailyPath, dailyContent, ct);
        await _index.IndexAsync(dailyNode, ct);
        await UpdateGraphAsync(dailyNode, extraction, ct);

        var indexPath = $"session/{candidate.SessionId}/index.md";
        var line = $"- {DateTimeOffset.UtcNow:HH:mm:ss} [{extraction.Kind}] {extraction.Title} → [[{dailyPath}]]\n";
        await AppendSessionIndexAsync(indexPath, line, ct);

        _logger?.LogInformation("Stored memory {Path} for session {SessionId}", dailyPath, candidate.SessionId);
        return new PipelineResult(true, dailyPath, null);
    }

    private async Task AppendSessionIndexAsync(string path, string line, CancellationToken ct)
    {
        var gate = _sessionLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await _fileStore.ReadAsync(path, ct);
            var body = existing?.Content ?? """
                ---
                type: session
                title: session-index
                ---

                # Session memory index

                """;
            if (!body.EndsWith('\n'))
                body += "\n";
            await _fileStore.WriteAsync(path, body + line, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// 更新知识图谱（节点 + Wikilink 边 + 目录父子边 + 标签边）
    /// </summary>
    private async Task UpdateGraphAsync(FileNode node, ExtractionResult extraction, CancellationToken ct)
    {
        await _graph.AddNodeAsync(node.Path, node.Metadata.Title ?? node.Path, ct);

        foreach (var link in WikilinkParser.Parse(extraction.Content))
        {
            var target = ResolveLinkPath(node.Path, link);
            await _graph.AddEdgeAsync(node.Path, target, EdgeType.Reference, ct: ct);
        }

        var dir = Path.GetDirectoryName(node.Path)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir) && dir != ".")
            await _graph.AddEdgeAsync(dir, node.Path, EdgeType.ParentChild, ct: ct);

        foreach (var tag in extraction.Tags)
        {
            var tagPath = $"tag/{tag}";
            await _graph.AddNodeAsync(tagPath, $"#{tag}", ct);
            await _graph.AddEdgeAsync(node.Path, tagPath, EdgeType.Tag, ct: ct);
        }
    }

    private static string ResolveLinkPath(string sourcePath, string link)
    {
        if (link.Contains('/') || link.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return link;

        var sourceDir = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "";
        return sourceDir.Length > 0 ? $"{sourceDir}/{link}.md" : $"{link}.md";
    }
}
