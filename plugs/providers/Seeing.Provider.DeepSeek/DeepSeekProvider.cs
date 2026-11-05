using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;

namespace Seeing.Provider.DeepSeek;

/// <summary>
/// DeepSeek Provider：OpenAI 兼容端点。
/// <para><b>默认只供 chat</b>：DeepSeek 官方端点没有 <c>/v1/embeddings</c>。
/// 若使用自建或代理端点确实提供了该能力，可在插件配置里写
/// <c>"capabilities": ["chat", "embedding"]</c> 覆盖。</para>
/// </summary>
public sealed class DeepSeekProvider : ConfigurableProviderBase<DeepSeekOptions>
{
    public const string ExtensionId = "seeing.provider.deepseek";

    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly DeepSeekConfigStore _store;
    private readonly IProviderRegistry _registry;
    private readonly DeepSeekModelsClient _modelsClient;
    private readonly IModelCapabilityManager _capabilityManager;
    private readonly object _gate = new();
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;

    public DeepSeekProvider(
        DeepSeekConfigStore store,
        ILlmClientFactoryResolver factories,
        IProviderRegistry registry,
        DeepSeekModelsClient modelsClient,
        IModelCapabilityManager capabilityManager,
        ILogger<DeepSeekProvider> logger)
        : base(factories, logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
    }

    public override string Id => "deepseek";

    public override string? Name => "DeepSeek";

    /// <inheritdoc />
    /// <remarks>DeepSeek 提供 OpenAI 兼容端点，故协议为 <c>openai</c>。</remarks>
    public override string Protocol => ProviderTypes.OpenAi;

    /// <summary>作者默认能力：只有 chat（官方端点不提供 embeddings）。</summary>
    protected override LlmCapabilityKind DefaultCapabilities => LlmCapabilityKind.Chat;

    /// <summary>无 API Key 时不创建客户端，连接测试直接失败。</summary>
    protected override bool HasUsableCredentials => !string.IsNullOrWhiteSpace(Options.ApiKey);

    /// <inheritdoc />
    protected override IReadOnlyList<string>? ReadCapabilityOverride(DeepSeekOptions options)
        => options.Capabilities;

    /// <inheritdoc />
    protected override ProviderConfig BuildProviderConfig() => new()
    {
        Id = Id,
        Type = Protocol,
        Name = Name,
        BaseUrl = DeepSeekModelsClient.DefaultBaseUrl,
        ApiKey = Options.ApiKey,
    };

    /// <inheritdoc />
    protected override ILlmCapabilityClient? CreateClient(
        ProviderConfig config,
        LlmCapabilityKind capability)
        => CreateClientViaFactory(config, capability);

    /// <inheritdoc />
    protected override async Task<DeepSeekOptions> LoadOptionsAsync(CancellationToken cancellationToken)
        => await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task SaveOptionsAsync(DeepSeekOptions options, CancellationToken cancellationToken)
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
        // 重新注册让 ProviderInfo 的能力投影跟上新配置（注册表内部是幂等替换）。
        _registry.Register(new ProviderRegistration(this, ProviderOrigin.Extension, OwnerId: ExtensionId));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        DeepSeekOptions options,
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
