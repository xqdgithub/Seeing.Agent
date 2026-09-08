using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;

namespace Seeing.Agent.Llm.Anthropic;

/// <summary>
/// Anthropic 协议 LLM 客户端工厂。
/// </summary>
public sealed class AnthropicLlmClientFactory : ILlmClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public AnthropicLlmClientFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.Anthropic };

    /// <inheritdoc />
    public bool SupportsType(string type) =>
        !string.IsNullOrWhiteSpace(type) && SupportedTypes.Contains(type);

    /// <inheritdoc />
    public ILlmClient Create(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!SupportsType(config.Type))
            throw new NotSupportedException($"不支持的 Provider 类型: {config.Type}");

        var httpClient = LlmHttpClientFactory.Create(config);
        return new AnthropicClient(
            config,
            httpClient,
            _loggerFactory.CreateLogger<AnthropicClient>());
    }
}
