using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// <see cref="ITextCompletion"/> 默认实现，委托给 <see cref="ILlmService"/>。
/// </summary>
public sealed class TextCompletionService : ITextCompletion
{
    private readonly ILlmService _llm;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<TextCompletionService>? _logger;

    public TextCompletionService(
        ILlmService llm,
        IOptions<SeeingAgentOptions> options,
        ILogger<TextCompletionService>? logger = null)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        CancellationToken ct = default)
    {
        var modelId = model ?? _options.Value.DefaultModel;
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("No model configured for text completion (DefaultModel is empty).");

        var request = new ChatRequest
        {
            Model = modelId,
            SystemPrompt = systemPrompt,
            Temperature = 0,
            MaxTokens = 2048,
            Messages =
            {
                new ChatMessage { Role = ChatRole.User, Content = userPrompt }
            }
        };

        var response = await _llm.CompleteAsync(modelId, request, ct).ConfigureAwait(false);
        var text = response.Message.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            _logger?.LogDebug("ITextCompletion returned empty content for model {Model}", modelId);
        return text;
    }
}
