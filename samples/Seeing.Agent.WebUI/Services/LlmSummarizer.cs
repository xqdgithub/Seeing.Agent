using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;
using Seeing.Session.Compression;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// LLM-based summarizer implementation for compression strategies.
/// </summary>
public class LlmSummarizer : ISummarizer
{
    private readonly ILlmService _llmService;
    private readonly ILogger<LlmSummarizer>? _logger;
    private readonly string? _modelId;

    private const string SummarizePromptTemplate = """
        Please summarize the following conversation history concisely, preserving:
        1. Main user requests and goals
        2. Key decisions and completed tasks
        3. Important context (file paths, configurations, etc.)
        4. Unresolved issues or pending items
        
        Keep the summary in Chinese, concise, under 500 characters.
        
        Conversation history:
        {text}
        """;

    public LlmSummarizer(
        ILlmService llmService,
        ILogger<LlmSummarizer>? logger = null,
        string? modelId = null)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _logger = logger;
        _modelId = modelId ?? GetDefaultModel(llmService);
    }

    public async Task<string> SummarizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(_modelId))
        {
            throw new InvalidOperationException("No LLM model available for summarization");
        }

        try
        {
            var prompt = SummarizePromptTemplate.Replace("{text}", text);
            
            var request = new ChatRequest
            {
                Messages = new List<ChatMessage>
                {
                    new() { Role = ChatRole.User, Content = prompt }
                },
                MaxTokens = 500,
                Temperature = 0.3
            };

            var response = await _llmService.CompleteAsync(_modelId, request, cancellationToken: cancellationToken);
            
            return response.Message?.Content ?? "(Summary generation failed)";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate summary");
            throw;
        }
    }

    private static string? GetDefaultModel(ILlmService llmService)
    {
        var models = llmService.GetAvailableModels();
        if (models.Count == 0)
        {
            return null;
        }

        // Prefer low-cost models for summarization
        var preferred = new[] { "gpt-4o-mini", "gpt-3.5-turbo", "claude-3-haiku", "deepseek-chat", "glm-4-flash" };
        foreach (var name in preferred)
        {
            if (models.ContainsKey(name))
            {
                return name;
            }
        }

        return models.Keys.First();
    }
}
