using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;

namespace Seeing.Agent.Llm.OpenAI;

/// <summary>
/// OpenAI 协议 LLM 客户端工厂。
/// </summary>
public sealed class OpenAiLlmClientFactory : ILlmClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCallInterceptorRegistry? _interceptors;
    private readonly ILlmClientDecoratorRegistry? _decorators;

    public OpenAiLlmClientFactory(
        ILoggerFactory loggerFactory,
        ILlmCallInterceptorRegistry? interceptors = null,
        ILlmClientDecoratorRegistry? decorators = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _interceptors = interceptors;
        _decorators = decorators;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.OpenAi };

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
        ILlmClient client = new OpenAiChatClient(
            config,
            httpClient,
            _loggerFactory.CreateLogger<OpenAiChatClient>(),
            _interceptors);

        return _decorators?.Apply(client, config) ?? client;
    }
}
