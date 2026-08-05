using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Evolution;

public sealed class LlmMemoryExtractor : IMemoryExtractor
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITextCompletion _completion;
    private readonly IOptions<MemoryOptions> _options;
    private readonly ILogger<LlmMemoryExtractor>? _logger;

    public LlmMemoryExtractor(
        ITextCompletion completion,
        IOptions<MemoryOptions> options,
        ILogger<LlmMemoryExtractor>? logger = null)
    {
        _completion = completion;
        _options = options;
        _logger = logger;
    }

    public async Task<ExtractionResult?> ExtractAsync(MemoryCandidate candidate, CancellationToken ct = default)
    {
        var results = await ExtractBatchAsync(new[] { candidate }, ct);
        return results.Count > 0 ? results[0] : null;
    }

    public async Task<IReadOnlyList<ExtractionResult>> ExtractBatchAsync(
        IReadOnlyList<MemoryCandidate> candidates,
        CancellationToken ct = default)
    {
        var extraction = _options.Value.Extraction;
        if (!extraction.Enabled || candidates.Count == 0)
            return Array.Empty<ExtractionResult>();

        try
        {
            var user = BuildBatchUserPrompt(candidates);
            var text = await _completion.CompleteAsync(
                PromptTemplates.ExtractionSystem,
                user,
                extraction.Model,
                ct: ct);

            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<ExtractionResult>();

            text = StripCodeFence(text.Trim());
            text = LlmMemoryEvolution.ExtractJsonPayloadForTests(text);
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<ExtractionResult>();

            BatchDto? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<BatchDto>(text, JsonOptions);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // 兼容旧单对象格式
                try
                {
                    var single = System.Text.Json.JsonSerializer.Deserialize<ItemDto>(text, JsonOptions);
                    if (single is not null)
                        parsed = new BatchDto { Items = new List<ItemDto> { single } };
                    else
                        throw;
                }
                catch (System.Text.Json.JsonException)
                {
                    _logger?.LogWarning(ex, "Memory extraction JSON parse failed for batch size {Count}", candidates.Count);
                    return Array.Empty<ExtractionResult>();
                }
            }

            if (parsed?.Items is not { Count: > 0 })
                return Array.Empty<ExtractionResult>();

            var results = new List<ExtractionResult>();
            foreach (var item in parsed.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Content))
                    continue;
                if (item.Importance < extraction.MinImportance)
                    continue;

                results.Add(new ExtractionResult(
                    string.IsNullOrWhiteSpace(item.Title)
                        ? item.Content[..Math.Min(40, item.Content.Length)]
                        : item.Title!,
                    item.Content.Trim(),
                    Math.Clamp(item.Importance, 0, 1),
                    item.Tags is { Count: > 0 } tags ? tags : Array.Empty<string>(),
                    string.IsNullOrWhiteSpace(item.Kind) ? "fact" : item.Kind!));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Memory batch extraction failed for {Count} candidates", candidates.Count);
            return Array.Empty<ExtractionResult>();
        }
    }

    private static string BuildBatchUserPrompt(IReadOnlyList<MemoryCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session={candidates[0].SessionId}");
        sb.AppendLine($"SnippetCount={candidates.Count}");
        sb.AppendLine();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.AppendLine($"--- snippet {i + 1} ---");
            sb.AppendLine($"Source={c.Source}");
            if (!string.IsNullOrEmpty(c.ToolId))
                sb.AppendLine($"Tool={c.ToolId}");
            sb.AppendLine(c.Snippet);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```", StringComparison.Ordinal))).Trim();
    }

    private sealed class BatchDto
    {
        public List<ItemDto>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public string? Title { get; set; }
        public string? Content { get; set; }
        public double Importance { get; set; }
        public List<string>? Tags { get; set; }
        public string? Kind { get; set; }
    }
}
