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
        var extraction = _options.Value.Extraction;
        if (!extraction.Enabled)
            return null;

        try
        {
            var user = $"Source={candidate.Source}\nTool={candidate.ToolId}\n\n{candidate.Snippet}";
            var text = await _completion.CompleteAsync(
                PromptTemplates.ExtractionSystem,
                user,
                extraction.Model,
                ct);

            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = StripCodeFence(text.Trim());
            if (string.IsNullOrWhiteSpace(text))
                return null;

            ExtractionDto? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<ExtractionDto>(text, JsonOptions);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger?.LogWarning(ex, "Memory extraction JSON parse failed for {Id}", candidate.Id);
                return null;
            }
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Content))
                return null;

            if (parsed.Importance < extraction.MinImportance)
                return null;

            return new ExtractionResult(
                string.IsNullOrWhiteSpace(parsed.Title)
                    ? parsed.Content[..Math.Min(40, parsed.Content.Length)]
                    : parsed.Title!,
                parsed.Content.Trim(),
                Math.Clamp(parsed.Importance, 0, 1),
                parsed.Tags is { Count: > 0 } tags ? tags : Array.Empty<string>(),
                string.IsNullOrWhiteSpace(parsed.Kind) ? "fact" : parsed.Kind!);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Memory extraction failed for {Id}", candidate.Id);
            return null;
        }
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```", StringComparison.Ordinal))).Trim();
    }

    private sealed class ExtractionDto
    {
        public string? Title { get; set; }
        public string? Content { get; set; }
        public double Importance { get; set; }
        public List<string>? Tags { get; set; }
        public string? Kind { get; set; }
    }
}
