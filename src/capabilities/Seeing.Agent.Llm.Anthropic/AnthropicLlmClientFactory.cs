using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Anthropic.Clients;

namespace Seeing.Agent.Llm.Anthropic;

/// <summary>
/// Anthropic 协议 LLM 客户端工厂。
/// <para>Anthropic 当前只供 <see cref="LlmCapabilityKind.Chat"/> 一种能力；其余能力返回 <c>null</c>
/// （决议 19：以配置为准，工厂不实现即报空，由 Router 第 5 层报错，不做回退）。</para>
/// </summary>
public sealed class AnthropicLlmClientFactory : ILlmClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCallInterceptorRegistry? _interceptors;
    private readonly ILlmClientDecoratorRegistry? _decorators;

    public AnthropicLlmClientFactory(
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
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.Anthropic };

    /// <inheritdoc />
    public bool SupportsProtocol(string protocol) =>
        !string.IsNullOrWhiteSpace(protocol) && SupportedProtocols.Contains(protocol);

    /// <inheritdoc />
    public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 协议或能力任一不匹配都返回 null：契约规定不支持的组合不抛
        if (!SupportsProtocol(config.Type) || capability != LlmCapabilityKind.Chat)
            return null;

        var httpClient = LlmHttpClientFactory.Create(config);
        ILlmCapabilityClient client = new AnthropicClient(
            config,
            httpClient,
            _loggerFactory.CreateLogger<AnthropicClient>(),
            _interceptors);

        // 装饰器（重试等）的唯一应用点：工厂内部、返回之前（spec §7.3）。
        // 上移到 ConfiguredLlmProvider 会让直接调工厂的 plug provider 静默失去重试。
        return _decorators?.Apply(client, config) ?? client;
    }
}
