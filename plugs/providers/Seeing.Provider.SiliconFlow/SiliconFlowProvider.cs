using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;

namespace Seeing.Provider.SiliconFlow;

/// <summary>
/// SiliconFlow Provider：OpenAI 兼容端点，可同时供出 chat 与 embedding。
/// <para><b>能力来源</b>：默认值由本类声明（<see cref="DefaultCapabilities"/>，只有作者知道
/// 自家端点支持什么）；配置里的 <c>capabilities</c> 可覆盖。装完即可用，无需先改配置。</para>
/// <para><b>为什么不由工厂推导</b>：同一个 OpenAI 兼容工厂能造 chat/embedding，
/// 不代表某个具体端点实现了 <c>/v1/embeddings</c>（如 DeepSeek 就没有）。
/// 由工厂推导会把「协议支持」误当成「端点支持」。</para>
/// </summary>
public sealed class SiliconFlowProvider : ConfigurableProviderBase<SiliconFlowOptions>
{
    public const string ExtensionId = "seeing.provider.siliconflow";

    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly SiliconFlowConfigStore _store;
    private readonly IProviderRegistry _registry;
    private readonly SiliconFlowModelsClient _modelsClient;
    private readonly IModelCapabilityManager _capabilityManager;
    private readonly ILogger<SiliconFlowProvider> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;

    public SiliconFlowProvider(
        SiliconFlowConfigStore store,
        ILlmClientFactoryResolver factories,
        IProviderRegistry registry,
        SiliconFlowModelsClient modelsClient,
        IModelCapabilityManager capabilityManager,
        ILogger<SiliconFlowProvider> logger)
        : base(factories, logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override string Id => "siliconflow";

    public override string? Name => "SiliconFlow";

    /// <inheritdoc />
    /// <remarks>SiliconFlow 提供 OpenAI 兼容端点，故协议为 <c>openai</c>。</remarks>
    public override string Protocol => ProviderTypes.OpenAi;

    /// <summary>
    /// 作者默认能力：chat + embedding + 语音转写。
    /// <para>不含 rerank/语音合成——OpenAI 协议工厂目前没有 rerank 实现，声明了会在 Router 第 5 层报错。
    /// 语音合成同理未声明：工厂虽已实现 <c>/audio/speech</c>，但当前没有消费方，声明只是占位。</para>
    /// <para><b>语音转写已实测可用</b>：<c>POST /v1/audio/transcriptions</c> 接受
    /// <c>FunAudioLLM/SenseVoiceSmall</c>、<c>TeleAI/TeleSpeechASR</c>、
    /// <c>XingChenAGI/XingChenASR-V3.2</c> 等模型，与 <c>OpenAiSpeechTranscriptionClient</c> 形状一致。</para>
    /// </summary>
    protected override LlmCapabilityKind DefaultCapabilities =>
        LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding | LlmCapabilityKind.SpeechTranscription;

    /// <summary>无 API Key 时不创建客户端，连接测试直接失败。</summary>
    protected override bool HasUsableCredentials => !string.IsNullOrWhiteSpace(Options.ApiKey);

    /// <inheritdoc />
    protected override IReadOnlyList<string>? ReadCapabilityOverride(SiliconFlowOptions options)
        => options.Capabilities;

    /// <inheritdoc />
    protected override ProviderConfig BuildProviderConfig() => new()
    {
        Id = Id,
        Type = Protocol,
        Name = Name,
        BaseUrl = SiliconFlowModelsClient.DefaultBaseUrl,
        ApiKey = Options.ApiKey,
    };

    /// <inheritdoc />
    protected override ILlmCapabilityClient? CreateClient(
        ProviderConfig config,
        LlmCapabilityKind capability)
        // 装饰器（重试/退避）由工厂在返回前应用，本 Provider 只原样交出（spec §7.3）。
        => CreateClientViaFactory(config, capability);

    /// <inheritdoc />
    protected override async Task<SiliconFlowOptions> LoadOptionsAsync(CancellationToken cancellationToken)
        => await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task SaveOptionsAsync(SiliconFlowOptions options, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _modelsCache = null;
            _modelsCachedAt = default;
        }
    }

    /// <inheritdoc />
    protected override Task OnOptionsSavedAsync(CancellationToken cancellationToken)
    {
        // 重新注册让 ProviderInfo 的能力投影跟上新配置（能力覆盖可能已变）。
        _registry.Register(new ProviderRegistration(this, ProviderOrigin.Extension, OwnerId: ExtensionId));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        SiliconFlowOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["ApiKey"] = options.ApiKey,
            ["Capabilities"] = options.Capabilities is null
                ? null
                : new List<string>(options.Capabilities),
        };

        return Task.FromResult(values);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKey = Options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return Array.Empty<ModelConfig>();

        lock (_gate)
        {
            if (_modelsCache is not null &&
                DateTimeOffset.Now - _modelsCachedAt < ModelsCacheTtl)
            {
                return _modelsCache;
            }
        }

        var models = (await _modelsClient.ListModelsAsync(apiKey, cancellationToken)
            .ConfigureAwait(false)).ToList();

        for (var i = 0; i < models.Count; i++)
        {
            models[i] = await _capabilityManager
                .TryEnrichIfEnabledAsync(models[i], cancellationToken)
                .ConfigureAwait(false);
        }

        lock (_gate)
        {
            _modelsCache = models;
            _modelsCachedAt = DateTimeOffset.Now;
        }

        return models;
    }
}
