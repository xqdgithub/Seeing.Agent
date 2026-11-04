using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using System.Text.Json;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekProvider : ILlmProvider, IConfigurableLlmProvider, IAsyncDisposable
{
    public const string ExtensionId = "seeing.provider.deepseek";
    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly DeepSeekConfigStore _store;
    private readonly IReadOnlyList<ILlmClientFactory> _factories;
    private readonly IProviderRegistry _registry;
    private readonly DeepSeekModelsClient _modelsClient;
    private readonly IModelCapabilityManager _capabilityManager;
    private readonly ILogger<DeepSeekProvider> _logger;
    private readonly object _gate = new();
    private string? _apiKey;
    private ILlmCapabilityClient? _client;
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;
    private int _disposed;

    public DeepSeekProvider(
        DeepSeekConfigStore store,
        IEnumerable<ILlmClientFactory> factories,
        IProviderRegistry registry,
        DeepSeekModelsClient modelsClient,
        IModelCapabilityManager capabilityManager,
        ILogger<DeepSeekProvider> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factories = (factories ?? throw new ArgumentNullException(nameof(factories))).ToArray();
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Id => "deepseek";

    public string? Name => "DeepSeek";

    /// <inheritdoc />
    /// <remarks>DeepSeek 提供 OpenAI 兼容端点，故协议为 <c>openai</c>。</remarks>
    public string Protocol => ProviderTypes.OpenAi;

    /// <summary>
    /// 只供 chat（<see cref="ProviderConfig.Capabilities"/> 的运行时投影）：
    /// embedding 等能力未经协议工厂实现，虚报会让 Router 在第 5 层报错。
    /// </summary>
    public LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;

    public int MaxRetries => 3;

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var options = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _apiKey = options.ApiKey;
        }
    }

    public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability)
    {
        // 不供出的能力返回 null（契约规定不抛、不回退）
        if (capability != LlmCapabilityKind.Chat)
            return null;

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _client ??= CreateClient();
        }
    }

    public async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        string? apiKey;
        lock (_gate)
        {
            apiKey = _apiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                return Array.Empty<ModelConfig>();

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
            if (string.Equals(_apiKey, apiKey, StringComparison.Ordinal))
            {
                _modelsCache = models;
                _modelsCachedAt = DateTimeOffset.Now;
            }
        }

        return models;
    }

    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema()
        => OptionsSchemaBuilder.FromType(typeof(DeepSeekOptions));

    public async Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        var options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["ApiKey"] = options.ApiKey
        };
    }

    public async Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        _ = level;

        var apiKey = GetString(values, "ApiKey");
        await _store.SaveAsync(
            new DeepSeekOptions { ApiKey = apiKey },
            cancellationToken).ConfigureAwait(false);

        ILlmCapabilityClient? oldClient;
        lock (_gate)
        {
            _apiKey = apiKey;
            _modelsCache = null;
            _modelsCachedAt = default;
            oldClient = InvalidateClient();
        }

        await DisposeClientAsync(oldClient).ConfigureAwait(false);
        _registry.Register(this, ExtensionId);
    }

    public async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return false;
        }

        // Provider 不推导、不猜能力：直接取该能力客户端，不供出即 false（spec §7.3 / 决议 9）
        var client = GetClient(capability);
        if (client is null)
            return false;

        return await client.TestConnectionAsync(
            modelId, capability, call: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        ILlmCapabilityClient? client;
        lock (_gate)
        {
            if (_disposed != 0)
                return;

            Volatile.Write(ref _disposed, 1);
            client = InvalidateClient();
            _modelsCache = null;
        }

        await DisposeClientAsync(client).ConfigureAwait(false);
    }

    private ILlmCapabilityClient? CreateClient()
    {
        try
        {
            var factory = LlmClientFactoryResolver.Require(_factories, Protocol);
            // 装饰器（重试 / 429 退避）由工厂在返回前应用，本 Provider 只原样交出（spec §7.3）。
            // plug provider 直接调工厂、不经 ConfiguredLlmProvider，故不可把 Apply 上移。
            return factory.Create(new ProviderConfig
            {
                Id = Id,
                Type = Protocol,
                Name = Name,
                BaseUrl = DeepSeekModelsClient.DefaultBaseUrl,
                ApiKey = _apiKey
            }, LlmCapabilityKind.Chat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建 DeepSeek Provider 客户端失败");
            throw;
        }
    }

    private ILlmCapabilityClient? InvalidateClient()
    {
        var oldClient = _client;
        _client = null;
        return oldClient;
    }

    private async ValueTask DisposeClientAsync(ILlmCapabilityClient? client)
    {
        if (client is null)
            return;

        try
        {
            switch (client)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 DeepSeek Provider 客户端失败");
        }
    }

    private static string? GetString(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => raw.ToString()
        };
    }
}
