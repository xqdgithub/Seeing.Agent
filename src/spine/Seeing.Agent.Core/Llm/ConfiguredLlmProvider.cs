using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.ConfigSchema;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 基于 <see cref="ProviderConfig"/> 创建客户端并提供模型目录的 LLM Provider。
/// </summary>
public sealed class ConfiguredLlmProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    private ProviderConfig _config;
    private readonly ILogger _logger;
    private readonly Func<ProviderConfig, ConfigLevel, CancellationToken, Task> _saveAsync;
    private readonly IModelCapabilityManager _capabilityManager;
    private readonly Lazy<ILlmClient> _client;
    private int _disposed;

    /// <summary>
    /// 使用 ProviderConfig 与客户端工厂构造配置驱动 Provider，持有配置独立副本并延迟创建客户端。
    /// </summary>
    public ConfiguredLlmProvider(
        ProviderConfig config,
        ILlmClientFactory factory,
        ILogger logger,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task> saveAsync,
        IModelCapabilityManager capabilityManager)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(capabilityManager);

        // 持有独立副本，避免与 SeeingAgent.Providers 字典共享引用
        _config = CloneConfig(config);
        _logger = logger;
        _saveAsync = saveAsync;
        _capabilityManager = capabilityManager;
        _client = new Lazy<ILlmClient>(
            () => CreateClient(factory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Provider 唯一标识（取自配置 Id）。
    /// </summary>
    public override string Id => _config.Id;

    /// <summary>
    /// Provider 显示名称（取自配置 Name）。
    /// </summary>
    public override string? Name => _config.Name;

    /// <summary>
    /// 配置声明的最大重试次数。
    /// </summary>
    public override int MaxRetries => _config.MaxRetries;

    /// <summary>
    /// 返回延迟创建的底层 LLM 客户端。
    /// </summary>
    public override ILlmClient GetClient() => _client.Value;

    /// <summary>
    /// 获取配置字段 schema；本 Provider 无自定义 schema，恒返回 null。
    /// </summary>
    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => null;

    /// <summary>
    /// 导出当前 Provider 配置为键值字典，供配置界面加载回填。
    /// </summary>
    public Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["Name"] = _config.Name,
            ["Type"] = _config.Type,
            ["BaseUrl"] = _config.BaseUrl,
            ["ApiKey"] = _config.ApiKey,
            ["Proxy"] = _config.Proxy,
            ["UseProxy"] = _config.UseProxy,
            ["Headers"] = _config.Headers is null
                ? null
                : new Dictionary<string, string>(_config.Headers),
            ["Timeout"] = _config.Timeout,
            ["MaxRetries"] = _config.MaxRetries,
            ["RetryBaseDelayMs"] = _config.RetryBaseDelayMs,
            ["RetryMaxDelayMs"] = _config.RetryMaxDelayMs,
            ["RetryTotalBudgetMs"] = _config.RetryTotalBudgetMs,
            ["DefaultModel"] = _config.DefaultModel
        };

        return Task.FromResult(values);
    }

    /// <summary>
    /// 将键值字典合并进现有配置副本，持久化到指定配置级别后替换当前配置。
    /// </summary>
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

    /// <summary>
    /// 返回配置内模型的独立副本，并按开关逐条应用能力元数据增强。
    /// </summary>
    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken)
    {
        var models = _config.Models?
            .Select(pair => CloneModel(pair.Key, pair.Value))
            .ToList() ?? [];

        for (var i = 0; i < models.Count; i++)
        {
            models[i] = await _capabilityManager
                .TryEnrichIfEnabledAsync(models[i], cancellationToken)
                .ConfigureAwait(false);
        }

        return models;
    }

    /// <summary>
    /// 通过底层客户端测试与指定模型的连通性。
    /// </summary>
    public override Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
        => GetClient().TestConnectionAsync(modelId, call: null, cancellationToken);

    /// <summary>
    /// 释放已创建的客户端（支持异步与同步 Dispose），未创建时为空操作。
    /// </summary>
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

        if (TryGetString(values, "Type", out var typeName) && !string.IsNullOrWhiteSpace(typeName))
            config.Type = typeName;

        if (TryGetString(values, "BaseUrl", out var baseUrl))
            config.BaseUrl = baseUrl;

        if (TryGetString(values, "ApiKey", out var apiKey))
            config.ApiKey = apiKey;

        if (TryGetString(values, "Proxy", out var proxy))
            config.Proxy = proxy;

        if (TryGetBool(values, "UseProxy", out var useProxy))
            config.UseProxy = useProxy;

        if (TryGetHeaders(values, "Headers", out var headers))
            config.Headers = headers;

        if (TryGetInt(values, "Timeout", out var timeout))
            config.Timeout = timeout;

        if (TryGetInt(values, "MaxRetries", out var maxRetries))
            config.MaxRetries = maxRetries;

        if (TryGetInt(values, "RetryBaseDelayMs", out var retryBaseDelayMs))
            config.RetryBaseDelayMs = retryBaseDelayMs;

        if (TryGetInt(values, "RetryMaxDelayMs", out var retryMaxDelayMs))
            config.RetryMaxDelayMs = retryMaxDelayMs;

        if (TryGetInt(values, "RetryTotalBudgetMs", out var retryTotalBudgetMs))
            config.RetryTotalBudgetMs = retryTotalBudgetMs;

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

    private static bool TryGetHeaders(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out Dictionary<string, string>? result)
    {
        result = null;
        if (!values.TryGetValue(key, out var raw))
            return false;

        if (raw is null)
            return true;

        if (raw is IReadOnlyDictionary<string, string> readOnlyHeaders)
        {
            result = new Dictionary<string, string>(readOnlyHeaders);
            return true;
        }

        if (raw is IDictionary<string, string> headers)
        {
            result = new Dictionary<string, string>(headers);
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Object } jsonObject)
        {
            result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in jsonObject.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
            return true;
        }

        throw new ArgumentException("Headers must be a dictionary of header names and values.", key);
    }

    private static bool TryGetBool(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out bool result)
    {
        result = default;
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case bool value:
                result = value;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                result = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                result = false;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } je
                when bool.TryParse(je.GetString(), out var fromJsonString):
                result = fromJsonString;
                return true;
            case string value when bool.TryParse(value, out var fromString):
                result = fromString;
                return true;
            default:
                return false;
        }
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
            Proxy = config.Proxy,
            UseProxy = config.UseProxy,
            DefaultModel = config.DefaultModel,
            Timeout = config.Timeout,
            MaxRetries = config.MaxRetries,
            RetryBaseDelayMs = config.RetryBaseDelayMs,
            RetryMaxDelayMs = config.RetryMaxDelayMs,
            RetryTotalBudgetMs = config.RetryTotalBudgetMs,
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
            Pricing = model.Pricing,
            Metadata = model.Metadata is null
                ? null
                : new Dictionary<string, object?>(model.Metadata)
        };
}
