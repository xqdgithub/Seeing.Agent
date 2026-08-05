using System.Collections.Concurrent;
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
        var batchResult = await ProcessBatchAsync(
            new MemoryBatch(
                candidate.Id,
                candidate.SessionId,
                new[] { candidate },
                candidate.CreatedAt),
            ct);

        if (batchResult.StoredCount > 0)
            return new PipelineResult(true, batchResult.DailyPaths?.FirstOrDefault(), null);

        return new PipelineResult(false, null, batchResult.Reason ?? "extract_skipped");
    }

    public async Task<BatchPipelineResult> ProcessBatchAsync(MemoryBatch batch, CancellationToken ct = default)
    {
        if (!_options.Value.Enabled)
            return new BatchPipelineResult(0, "disabled");

        if (!_options.Value.Extraction.Enabled)
            return new BatchPipelineResult(0, "extraction_disabled");

        if (batch.Candidates.Count == 0)
            return new BatchPipelineResult(0, "empty");

        // 入缓冲前通常已过滤；此处再滤一次以兼容直接 ProcessAsync 调用
        var accepted = new List<MemoryCandidate>(batch.Candidates.Count);
        foreach (var candidate in batch.Candidates)
        {
            var decision = _filter.Evaluate(candidate);
            if (decision.Accepted)
                accepted.Add(candidate);
        }

        if (accepted.Count == 0)
            return new BatchPipelineResult(0, "filtered");

        var extractions = await _extractor.ExtractBatchAsync(accepted, ct);
        if (extractions.Count == 0)
            return new BatchPipelineResult(0, "extract_skipped");

        var storedPaths = new List<string>();
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        foreach (var extraction in extractions)
        {
            var id = Guid.NewGuid().ToString("N");
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
                source_session: {batch.SessionId}
                created_at: {DateTimeOffset.Now:O}
                ---

                {extraction.Content}
                """;

            var dailyNode = await _fileStore.WriteAsync(dailyPath, dailyContent, ct);
            await _index.IndexAsync(dailyNode, ct);
            await UpdateGraphAsync(dailyNode, extraction, ct);

            var indexPath = $"session/{batch.SessionId}/index.md";
            var line = $"- {DateTimeOffset.Now:HH:mm:ss} [{extraction.Kind}] {extraction.Title} → [[{dailyPath}]]\n";
            await AppendSessionIndexAsync(indexPath, line, ct);
            storedPaths.Add(dailyPath);
        }

        _logger?.LogInformation(
            "Stored {Count} memories for session {SessionId} from batch {BatchId}",
            storedPaths.Count,
            batch.SessionId,
            batch.Id);
        return new BatchPipelineResult(storedPaths.Count, null, storedPaths);
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
