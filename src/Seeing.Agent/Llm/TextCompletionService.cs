using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// <see cref="ITextCompletion"/> 默认实现：经 <see cref="ILlmService.CompleteRawAsync"/> 旁路补全，不触发 Hook。
/// </summary>
public sealed class TextCompletionService : ITextCompletion
{
    private readonly ILlmService _llm;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ILogger<TextCompletionService>? _logger;

    public TextCompletionService(
        ILlmService llm,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<TextCompletionService>? logger = null)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
    }

    public const int DefaultMaxTokens = 64;

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = userPrompt }
        };
        return CompleteAsync(systemPrompt, messages, model, maxTokens, ct);
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        List<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        var modelId = model ?? _options.CurrentValue.DefaultModel;
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("No model configured for text completion (DefaultModel is empty).");

        var request = new ChatRequest
        {
            Model = modelId,
            SystemPrompt = systemPrompt,
            Temperature = 0,
            MaxTokens = maxTokens is > 0 ? maxTokens.Value : DefaultMaxTokens,
            Messages = messages
        };

        var response = await _llm.CompleteRawAsync(modelId, request, ct).ConfigureAwait(false);
        var text = response.Message.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _logger?.LogWarning(
                "ITextCompletion empty content: Model={Model}, MaxTokens={MaxTokens}, ReasoningLen={ReasoningLen}",
                modelId,
                request.MaxTokens,
                response.Message.ReasoningContent?.Length ?? 0);
        }
        return text;
    }
}
