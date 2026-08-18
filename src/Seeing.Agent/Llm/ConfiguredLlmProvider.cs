using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.ConfigSchema;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Llm;

/// <summary>
/// 基于 <see cref="ProviderConfig"/> 创建客户端并提供模型目录的 LLM Provider。
/// </summary>
public sealed class ConfiguredLlmProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    private ProviderConfig _config;
    private readonly ILogger _logger;
    private readonly Func<ProviderConfig, ConfigLevel, CancellationToken, Task> _saveAsync;
    private readonly Lazy<ILlmClient> _client;
    private int _disposed;

    public ConfiguredLlmProvider(
        ProviderConfig config,
        ILlmClientFactory factory,
        ILogger logger,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(saveAsync);

        // 持有独立副本，避免与 SeeingAgent.Providers 字典共享引用
        _config = CloneConfig(config);
        _logger = logger;
        _saveAsync = saveAsync;
        _client = new Lazy<ILlmClient>(
            () => CreateClient(factory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override string Id => _config.Id;

    public override string? Name => _config.Name;

    public override int MaxRetries => _config.MaxRetries;

    public override ILlmClient GetClient() => _client.Value;

    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => null;

    public Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["Name"] = _config.Name,
            ["Type"] = _config.Type.ToString(),
            ["BaseUrl"] = _config.BaseUrl,
            ["ApiKey"] = _config.ApiKey,
            ["Timeout"] = _config.Timeout,
            ["MaxRetries"] = _config.MaxRetries,
            ["DefaultModel"] = _config.DefaultModel
        };

        return Task.FromResult(values);
    }

    public async Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var updated = CloneConfig(_config);
        ApplyValues(updated, values);
        await _saveAsync(updated, level, cancellationToken).ConfigureAwait(false);
        _config = updated;
    }

    public override Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken)
    {
        var models = _config.Models?
            .Select(pair => CloneModel(pair.Key, pair.Value))
            .ToList() ?? [];
        return Task.FromResult<IReadOnlyList<ModelConfig>>(models);
    }

    public override Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
        => GetClient().TestConnectionAsync(modelId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!_client.IsValueCreated)
            return;

        try
        {
            switch (_client.Value)
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
            _logger.LogWarning(ex, "释放 Provider 客户端失败: {ProviderId}", Id);
        }
    }

    private ILlmClient CreateClient(ILlmClientFactory factory)
    {
        try
        {
            return CreateBuiltInClient(factory, _config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建 Provider 客户端失败: {ProviderId}", Id);
            throw;
        }
    }

    private static void ApplyValues(ProviderConfig config, IReadOnlyDictionary<string, object?> values)
    {
        if (TryGetString(values, "Name", out var name))
            config.Name = name;

        if (TryGetString(values, "Type", out var typeName) &&
            Enum.TryParse<ProviderType>(typeName, ignoreCase: true, out var type))
            config.Type = type;

        if (TryGetString(values, "BaseUrl", out var baseUrl))
            config.BaseUrl = baseUrl;

        if (TryGetString(values, "ApiKey", out var apiKey))
            config.ApiKey = apiKey;

        if (TryGetInt(values, "Timeout", out var timeout))
            config.Timeout = timeout;

        if (TryGetInt(values, "MaxRetries", out var maxRetries))
            config.MaxRetries = maxRetries;

        if (TryGetString(values, "DefaultModel", out var defaultModel))
            config.DefaultModel = defaultModel;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out string? result)
    {
        result = null;
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return false;

        result = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => raw.ToString()
        };
        return true;
    }

    private static bool TryGetInt(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out int result)
    {
        result = default;
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case int i:
                result = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case double d when d is >= int.MinValue and <= int.MaxValue:
                result = (int)d;
                return true;
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var fromJson))
                {
                    result = fromJson;
                    return true;
                }

                if (je.ValueKind == JsonValueKind.String &&
                    int.TryParse(je.GetString(), out fromJson))
                {
                    result = fromJson;
                    return true;
                }

                return false;
            case string s when int.TryParse(s, out var fromString):
                result = fromString;
                return true;
            default:
                return false;
        }
    }

    private static ProviderConfig CloneConfig(ProviderConfig config)
        => new()
        {
            Id = config.Id,
            Type = config.Type,
            Name = config.Name,
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey,
            DefaultModel = config.DefaultModel,
            Timeout = config.Timeout,
            MaxRetries = config.MaxRetries,
            Models = config.Models is null ? null : new Dictionary<string, ModelConfig>(config.Models),
            Options = config.Options is null ? null : new Dictionary<string, object>(config.Options),
            Headers = config.Headers is null ? null : new Dictionary<string, string>(config.Headers)
        };

    private ModelConfig CloneModel(string modelId, ModelConfig model)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(model.Id) ? modelId : model.Id,
            Name = model.Name,
            Provider = Id,
            Types = model.Types,
            Modalities = model.Modalities,
            Limit = model.Limit,
            Options = model.Options,
            Pricing = model.Pricing
        };
}
