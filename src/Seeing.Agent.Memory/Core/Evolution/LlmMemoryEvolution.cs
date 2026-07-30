using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Storage;

namespace Seeing.Agent.Memory.Core.Evolution;

public sealed class LlmMemoryEvolution : IMemoryEvolutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IFileStore _fileStore;
    private readonly IMemoryIndex _index;
    private readonly IMemoryGraph _graph;
    private readonly ITextCompletion _completion;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<LlmMemoryEvolution>? _logger;

    public LlmMemoryEvolution(
        IFileStore fileStore,
        IMemoryIndex index,
        IMemoryGraph graph,
        ITextCompletion completion,
        IOptions<MemoryOptions> options,
        ILogger<LlmMemoryEvolution>? logger = null)
    {
        _fileStore = fileStore;
        _index = index;
        _graph = graph;
        _completion = completion;
        _options = options;
        _logger = logger;
    }

    public async Task EvolveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var opts = _options.Value;
        if (!opts.Enabled || !opts.Evolution.Enabled)
            return;

        var files = await _fileStore.ListByPrefixAsync("daily", ct);
        var related = files
            .Where(f => f.Content.Contains($"source_session: {sessionId}", StringComparison.Ordinal)
                        || f.Metadata.Id.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (related.Count == 0)
            return;

        var bundle = string.Join("\n---\n", related.Select(f => f.Content));

        try
        {
            var text = await _completion.CompleteAsync(
                PromptTemplates.EvolutionSystem,
                bundle,
                opts.Extraction.Model,
                ct);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger?.LogDebug("Evolution empty response for session {SessionId}", sessionId);
                return;
            }

            text = StripFence(text.Trim());
            text = ExtractJsonPayload(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger?.LogDebug("Evolution no JSON payload for session {SessionId}", sessionId);
                return;
            }

            EvolutionDto? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<EvolutionDto>(text, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Evolution JSON parse failed for session {SessionId}", sessionId);
                return;
            }

            if (parsed?.Items is not { Count: > 0 })
                return;

            foreach (var item in parsed.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Content) || item.Importance < opts.Extraction.MinImportance)
                    continue;

                var id = Guid.NewGuid().ToString("N")[..12];
                var isDigest = item.Importance >= opts.Evolution.PromoteToDigestMinImportance;
                var path = isDigest
                    ? $"digest/{id}.md"
                    : $"daily/{DateTimeOffset.UtcNow:yyyy-MM-dd}/{id}.md";

                var content = $"""
                    ---
                    id: {id}
                    type: {(isDigest ? "digest" : "daily")}
                    title: "{item.Title?.Replace("\"", "'")}"
                    tags: [{string.Join(", ", item.Tags ?? new List<string>())}]
                    importance: {item.Importance:0.###}
                    source_session: {sessionId}
                    created_at: {DateTimeOffset.UtcNow:O}
                    ---

                    {item.Content}
                    """;

                var node = await _fileStore.WriteAsync(path, content, ct);
                await _index.IndexAsync(node, ct);
                await UpdateGraphForNodeAsync(node, item.Title ?? path, item.Tags, item.Content, ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Evolution failed for session {SessionId}", sessionId);
        }
    }

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```", StringComparison.Ordinal))).Trim();
    }

    /// <summary>从可能夹杂说明文字的回复中提取 JSON 对象/数组。</summary>
    internal static string ExtractJsonPayload(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return trimmed;

        var objStart = trimmed.IndexOf('{');
        var arrStart = trimmed.IndexOf('[');
        var start = (objStart, arrStart) switch
        {
            (< 0, < 0) => -1,
            (var o, < 0) => o,
            (< 0, var a) => a,
            (var o, var a) => Math.Min(o, a)
        };
        if (start < 0)
            return string.Empty;

        var open = trimmed[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        for (var i = start; i < trimmed.Length; i++)
        {
            if (trimmed[i] == open) depth++;
            else if (trimmed[i] == close)
            {
                depth--;
                if (depth == 0)
                    return trimmed[start..(i + 1)];
            }
        }

        return string.Empty;
    }

    // 测试入口
    public static string ExtractJsonPayloadForTests(string text) => ExtractJsonPayload(text);

    /// <summary>
    /// 为演化生成的节点更新知识图谱
    /// </summary>
    private async Task UpdateGraphForNodeAsync(FileNode node, string title,
        IReadOnlyList<string>? tags, string content, CancellationToken ct)
    {
        await _graph.AddNodeAsync(node.Path, title, ct);

        foreach (var link in WikilinkParser.Parse(content))
        {
            var target = ResolveLinkPath(node.Path, link);
            await _graph.AddEdgeAsync(node.Path, target, EdgeType.Reference, ct: ct);
        }

        var dir = Path.GetDirectoryName(node.Path)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(dir) && dir != ".")
            await _graph.AddEdgeAsync(dir, node.Path, EdgeType.ParentChild, ct: ct);

        if (tags != null)
        {
            foreach (var tag in tags)
            {
                var tagPath = $"tag/{tag}";
                await _graph.AddNodeAsync(tagPath, $"#{tag}", ct);
                await _graph.AddEdgeAsync(node.Path, tagPath, EdgeType.Tag, ct: ct);
            }
        }
    }

    private static string ResolveLinkPath(string sourcePath, string link)
    {
        if (link.Contains('/') || link.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return link;

        var sourceDir = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "";
        return sourceDir.Length > 0 ? $"{sourceDir}/{link}.md" : $"{link}.md";
    }

    private sealed class EvolutionDto
    {
        public List<ItemDto>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public string? Title { get; set; }
        public string? Content { get; set; }
        public double Importance { get; set; }
        public List<string>? Tags { get; set; }
    }
}
