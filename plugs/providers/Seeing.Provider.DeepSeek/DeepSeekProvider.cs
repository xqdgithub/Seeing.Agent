using Seeing.Agent.Abstractions.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    public const string ExtensionId = "seeing.provider.deepseek";
    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly DeepSeekConfigStore _store;
    private readonly ILlmClientFactory _factory;
    private readonly IProviderRegistry _registry;
    private readonly DeepSeekModelsClient _modelsClient;
    private readonly ILogger<DeepSeekProvider> _logger;
    private readonly object _gate = new();
    private string? _apiKey;
    private ILlmClient? _client;
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;
    private int _disposed;

    public DeepSeekProvider(
        DeepSeekConfigStore store,
        ILlmClientFactory factory,
        IProviderRegistry registry,
        DeepSeekModelsClient modelsClient,
        ILogger<DeepSeekProvider> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override string Id => "deepseek";

    public override string? Name => "DeepSeek";

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var options = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _apiKey = options.ApiKey;
        }
    }

    public override ILlmClient GetClient()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _client ??= CreateClient();
        }
    }

    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken)
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

        var models = await _modelsClient.ListModelsAsync(apiKey, cancellationToken)
            .ConfigureAwait(false);

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

        ILlmClient? oldClient;
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

    public override Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return Task.FromResult(false);
        }

        return GetClient().TestConnectionAsync(modelId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        ILlmClient? client;
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

    private ILlmClient CreateClient()
    {
        try
        {
            return CreateBuiltInClient(_factory, new ProviderConfig
            {
                Id = Id,
                Type = ProviderType.OpenAI,
                Name = Name,
                BaseUrl = DeepSeekModelsClient.DefaultBaseUrl,
                ApiKey = _apiKey
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建 DeepSeek Provider 客户端失败");
            throw;
        }
    }

    private ILlmClient? InvalidateClient()
    {
        var oldClient = _client;
        _client = null;
        return oldClient;
    }

    private async ValueTask DisposeClientAsync(ILlmClient? client)
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
