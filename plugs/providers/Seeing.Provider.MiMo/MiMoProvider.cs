using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;

namespace Seeing.Provider.MiMo;

/// <summary>
/// MiMo Provider：OpenAI 兼容端点。
/// <para><b>默认只供 chat</b>：官方端点没有 <c>/v1/embeddings</c>。
/// 若使用自建/代理端点确实提供该能力，可在插件配置里写
/// <c>"capabilities": ["chat", "embedding"]</c> 覆盖。</para>
/// </summary>
public sealed class MiMoProvider : ConfigurableProviderBase<MiMoOptions>
{
    public const string ExtensionId = "seeing.provider.mimo";

    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly MiMoConfigStore _store;
    private readonly IProviderRegistry _registry;
    private readonly MiMoModelsClient _modelsClient;
    private readonly IModelCapabilityManager _capabilityManager;
    private readonly object _gate = new();
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;

    public MiMoProvider(
        MiMoConfigStore store,
        ILlmClientFactoryResolver factories,
        IProviderRegistry registry,
        MiMoModelsClient modelsClient,
        IModelCapabilityManager capabilityManager,
        ILogger<MiMoProvider> logger)
        : base(factories, logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
    }

    public override string Id => "mimo";

    public override string? Name => "MiMo";

    /// <inheritdoc />
    /// <remarks>MiMo 提供 OpenAI 兼容端点，故协议为 <c>openai</c>。</remarks>
    public override string Protocol => ProviderTypes.OpenAi;

    /// <summary>作者默认能力：只有 chat（官方端点不提供 embeddings）。</summary>
    protected override LlmCapabilityKind DefaultCapabilities => LlmCapabilityKind.Chat;

    /// <summary>无 API Key 时不创建客户端，连接测试直接失败。</summary>
    protected override bool HasUsableCredentials => !string.IsNullOrWhiteSpace(Options.ApiKey);

    /// <inheritdoc />
    protected override IReadOnlyList<string>? ReadCapabilityOverride(MiMoOptions options)
        => options.Capabilities;

    /// <inheritdoc />
    protected override ProviderConfig BuildProviderConfig() => new()
    {
        Id = Id,
        Type = Protocol,
        Name = Name,
        BaseUrl = MiMoModelsClient.DefaultBaseUrl,
        ApiKey = Options.ApiKey,
    };

    /// <inheritdoc />
    protected override ILlmCapabilityClient? CreateClient(
        ProviderConfig config,
        LlmCapabilityKind capability)
        => CreateClientViaFactory(config, capability);

    /// <inheritdoc />
    protected override async Task<MiMoOptions> LoadOptionsAsync(CancellationToken cancellationToken)
        => await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task SaveOptionsAsync(MiMoOptions options, CancellationToken cancellationToken)
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
        _registry.Register(this, ExtensionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        MiMoOptions options,
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
