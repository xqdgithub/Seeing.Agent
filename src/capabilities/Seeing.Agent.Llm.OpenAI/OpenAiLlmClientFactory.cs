using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;

namespace Seeing.Agent.Llm.OpenAI;

/// <summary>
/// OpenAI 协议 LLM 客户端工厂：按「协议 + 能力」分派（spec §7.3）。
/// <para>支持 <see cref="ProviderTypes.OpenAi"/> 与 <see cref="ProviderTypes.OpenAiResponses"/> 两个协议
/// （决议 7）；Chat 能力按协议选择 Chat Completions 或 Responses 客户端，
/// Embedding / SpeechSynthesis / SpeechTranscription 走 OpenAI 兼容的共享端点，
/// 其余能力返回 <c>null</c>（不抛）。</para>
/// <para><b>装饰器（重试等）的唯一应用点在 <see cref="Create"/> 返回之前</b>（spec §7.3）：
/// 上移到 Provider 层会让直接调工厂的 plug provider 静默失去重试。</para>
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
    public IReadOnlySet<string> SupportedProtocols { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProviderTypes.OpenAi,
            ProviderTypes.OpenAiResponses
        };

    /// <inheritdoc />
    public bool SupportsProtocol(string protocol) =>
        !string.IsNullOrWhiteSpace(protocol) && SupportedProtocols.Contains(ProviderTypes.Normalize(protocol));

    /// <inheritdoc />
    public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!SupportsProtocol(config.Type))
            return null;

        // 每个客户端实例恰好实现一个能力端口（关卡 1 审查派发顺序 #3：D-1 装饰器靠 `is` 判定能力的前提）
        ILlmCapabilityClient? client = capability switch
        {
            LlmCapabilityKind.Chat => CreateChatClient(config),
            LlmCapabilityKind.Embedding => CreateEmbeddingClient(config),
            LlmCapabilityKind.SpeechSynthesis => CreateSpeechSynthesisClient(config),
            LlmCapabilityKind.SpeechTranscription => CreateSpeechTranscriptionClient(config),
            _ => null,
        };

        // 装饰器唯一应用点：Create 返回前
        return client is null ? null : _decorators?.Apply(client, config) ?? client;
    }

    /// <summary>Chat 按协议分流：openai → Chat Completions；openai-responses → Responses API（决议 7）。</summary>
    private ILlmCapabilityClient CreateChatClient(ProviderConfig config) =>
        ProviderTypes.Normalize(config.Type) == ProviderTypes.OpenAiResponses
            ? new OpenAiResponsesClient(
                config,
                LlmHttpClientFactory.Create(config),
                _loggerFactory.CreateLogger<OpenAiResponsesClient>(),
                _interceptors)
            : new OpenAiChatClient(
                config,
                LlmHttpClientFactory.Create(config),
                _loggerFactory.CreateLogger<OpenAiChatClient>(),
                _interceptors);

    private ILlmCapabilityClient CreateEmbeddingClient(ProviderConfig config) =>
        new OpenAiEmbeddingClient(
            config,
            LlmHttpClientFactory.Create(config),
            _loggerFactory.CreateLogger<OpenAiEmbeddingClient>(),
            _interceptors);

    private ILlmCapabilityClient CreateSpeechSynthesisClient(ProviderConfig config) =>
        new OpenAiSpeechSynthesisClient(
            config,
            LlmHttpClientFactory.Create(config),
            _loggerFactory.CreateLogger<OpenAiSpeechSynthesisClient>(),
            _interceptors);

    private ILlmCapabilityClient CreateSpeechTranscriptionClient(ProviderConfig config) =>
        new OpenAiSpeechTranscriptionClient(
            config,
            LlmHttpClientFactory.Create(config),
            _loggerFactory.CreateLogger<OpenAiSpeechTranscriptionClient>(),
            _interceptors);
}
