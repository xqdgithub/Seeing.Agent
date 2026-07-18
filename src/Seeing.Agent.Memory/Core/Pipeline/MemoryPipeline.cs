using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Pipeline;

public sealed class MemoryPipeline : IMemoryPipeline
{
    private readonly IMemoryHeuristicFilter _filter;
    private readonly IMemoryExtractor _extractor;
    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<MemoryPipeline>? _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public MemoryPipeline(
        IMemoryHeuristicFilter filter,
        IMemoryExtractor extractor,
        IFileStore fileStore,
        IMemoryIndex index,
        IOptions<MemoryOptions> options,
        ILogger<MemoryPipeline>? logger = null)
    {
        _filter = filter;
        _extractor = extractor;
        _fileStore = fileStore;
        _index = index;
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
}
