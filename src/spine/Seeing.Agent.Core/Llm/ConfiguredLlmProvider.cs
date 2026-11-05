using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using System.Text.Json;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 基于 <see cref="ProviderConfig"/> 创建客户端并提供模型目录的 LLM Provider。
/// <para>一个 Provider 按能力供出多个客户端（同 endpoint、同 key 可同时供 chat 与 embedding）：
/// 每个声明的能力一个 <see cref="Lazy{ILlmCapabilityClient}"/>，缓存键是
/// <c>(providerId, capability)</c>（spec §7.4）——同 Provider 下 100 个模型只有一个 embedding 客户端。</para>
/// <para><b>本类只做延迟缓存</b>：装饰器（重试等）在工厂内部应用（spec §7.3），
/// 上移会让直接调工厂的 plug provider 静默失去重试。</para>
/// </summary>
public sealed class ConfiguredLlmProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    private ProviderConfig _config;
    private readonly ILogger _logger;
    private readonly Func<ProviderConfig, ConfigLevel, CancellationToken, Task> _saveAsync;
    private readonly IModelCapabilityManager _capabilityManager;

    /// <summary>能力 → 延迟客户端；键集合就是构造时解析出的能力投影，不随保存热更（重建由 ProviderManager 负责）。</summary>
    private readonly Dictionary<LlmCapabilityKind, Lazy<ILlmCapabilityClient?>> _clients;

    private int _disposed;

    /// <summary>
    /// 使用 ProviderConfig 与客户端工厂构造配置驱动 Provider，持有配置独立副本并按声明能力延迟创建客户端。
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
        Capabilities = LlmCapabilityMap.ParseCapabilities(_config.Capabilities, _config.Type);
        _clients = BuildClientLazies(factory, _config, Capabilities, logger);
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
    /// 协议（取自配置 Type）。
    /// </summary>
    public override string Protocol => _config.Type;

    /// <summary>
    /// 配置声明的能力位集：<see cref="ProviderConfig.Capabilities"/> 的运行时投影（决议 15）。
    /// <para>未写 <c>capabilities</c> 时按协议缺省（只给 Chat）——旧配置升级后能力收缩是刻意的，
    /// 要 embedding 必须显式写。</para>
    /// </summary>
    public override LlmCapabilityKind Capabilities { get; }

    /// <summary>
    /// 配置声明的最大重试次数。
    /// </summary>
    public override int MaxRetries => _config.MaxRetries;

    /// <summary>
    /// 按能力取客户端：未声明该能力返回 <c>null</c>（不抛，Review Focus #1）；
    /// 声明了但工厂产不出客户端同样返回 <c>null</c>，由 Router 第 5 层给出可操作错误（决议 19）。
    /// </summary>
    public override ILlmCapabilityClient? GetClient(LlmCapabilityKind capability)
        => _clients.TryGetValue(capability, out var lazy) ? lazy.Value : null;

    /// <summary>
    /// 获取配置字段 schema；本 Provider 无自定义 schema，恒返回 null。
    /// </summary>
    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => null;

    /// <summary>
    /// 导出当前 Provider 配置为键值字典，供配置界面加载回填。
    /// <para><b>硬编码字段清单之一</b>：新增配置字段必须同步这里、
    /// <see cref="ApplyValues"/>、<see cref="CloneConfig"/> 与 <c>ProviderManager.CloneConfig</c> 四处，
    /// 漏一处就等于「保存一次抹掉配置」（Review Focus #5）。</para>
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
            ["Capabilities"] = _config.Capabilities is null
                ? null
                : new List<string>(_config.Capabilities),
            ["ListModels"] = _config.ListModels
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
    /// 返回「静态 <c>config.Models</c> ∪ 远端目录」的独立副本，并逐条应用能力元数据增强。
    /// <para>远端目录只在 <see cref="ProviderConfig.ListModels"/> 为 true 且有客户端实现
    /// <see cref="IModelCatalogClient"/> 时合并（spec §7.3）。静态条目优先：用户显式配置
    /// （限额、定价、思考档位）不被远端裸条目冲掉。</para>
    /// </summary>
    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken)
    {
        var models = _config.Models?
            .Select(pair => CloneModel(pair.Key, pair.Value))
            .ToList() ?? [];

        models = await EnrichAllAsync(models, cancellationToken).ConfigureAwait(false);

        if (!_config.ListModels)
            return models;

        var remote = await TryListRemoteModelsAsync(cancellationToken).ConfigureAwait(false);
        if (remote is null || remote.Count == 0)
            return models;

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_config.Models is not null)
        {
            foreach (var key in _config.Models.Keys)
                known.Add(key);
        }

        var additions = new List<ModelConfig>();
        foreach (var entry in remote)
        {
            var modelId = string.IsNullOrWhiteSpace(entry.Id) ? entry.Name : entry.Id;
            if (string.IsNullOrWhiteSpace(modelId) || !known.Add(modelId))
                continue;

            additions.Add(CloneModel(modelId, entry));
        }

        if (additions.Count == 0)
            return models;

        var merged = new List<ModelConfig>(models.Count + additions.Count);
        merged.AddRange(models);
        merged.AddRange(await EnrichAllAsync(additions, cancellationToken).ConfigureAwait(false));
        return merged;
    }

    /// <summary>
    /// 测试指定模型在指定能力上的连通性：直接取该能力的客户端，不查目录、不猜（决议 9）。
    /// </summary>
    public override Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken)
    {
        var client = GetClient(capability);
        if (client is null)
        {
            _logger.LogWarning(
                "Provider 未供出该能力的客户端，跳过连接测试: {ProviderId} {Capability}",
                Id,
                LlmCapabilityMap.ToConfigKey(capability));
            return Task.FromResult(false);
        }

        return client.TestConnectionAsync(modelId, capability, call: null, cancellationToken);
    }

    /// <summary>
    /// 释放全部已创建的客户端（支持异步与同步 Dispose），未创建时为空操作。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var (capability, lazy) in _clients)
        {
            if (!lazy.IsValueCreated)
                continue;

            try
            {
                switch (lazy.Value)
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
                // 单个客户端释放失败不能阻断其余客户端：逐个记录后继续
                _logger.LogWarning(
                    ex,
                    "释放 Provider 客户端失败: {ProviderId} {Capability}",
                    Id,
                    LlmCapabilityMap.ToConfigKey(capability));
            }
        }
    }

    /// <summary>
    /// 为每个声明的单值能力建一个延迟客户端工厂。
    /// </summary>
    private static Dictionary<LlmCapabilityKind, Lazy<ILlmCapabilityClient?>> BuildClientLazies(
        ILlmClientFactory factory,
        ProviderConfig config,
        LlmCapabilityKind capabilities,
        ILogger logger)
    {
        var lazies = new Dictionary<LlmCapabilityKind, Lazy<ILlmCapabilityClient?>>();
        foreach (var kind in LlmCapabilityMap.SingleFlags(capabilities))
        {
            lazies[kind] = new Lazy<ILlmCapabilityClient?>(
                () =>
                {
                    try
                    {
                        return CreateBuiltInClient(factory, config, kind);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "创建 Provider 客户端失败: {ProviderId} {Capability}",
                            config.Id,
                            LlmCapabilityMap.ToConfigKey(kind));
                        throw;
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        return lazies;
    }

    /// <summary>
    /// 从已创建的客户端里找第一个 <see cref="IModelCatalogClient"/> 取远端目录。
    /// <para>找不到（该 Provider 没有动态目录端口）时返回 null 并记 Debug，不影响静态目录。</para>
    /// </summary>
    private async Task<IReadOnlyList<ModelConfig>?> TryListRemoteModelsAsync(CancellationToken cancellationToken)
    {
        foreach (var (capability, lazy) in _clients)
        {
            ILlmCapabilityClient? client;
            try
            {
                client = lazy.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "拉取远端模型目录时创建客户端失败: {ProviderId} {Capability}",
                    Id,
                    LlmCapabilityMap.ToConfigKey(capability));
                continue;
            }

            if (client is not IModelCatalogClient catalog)
                continue;

            try
            {
                var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);

                // 装饰基类无条件实现该端口：未实现目录的内层客户端会返回空集。
                // 因此不能「首个命中即返回」，否则第一个能力（枚举顺序常为 Chat）
                // 会把真正带目录的客户端（如 decision/typesafe）永远挡住。
                if (models.Count > 0)
                    return models;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 远端目录不可用不能让整张模型表消失：记警告后继续试下一个能力，
                // 全部失败时由调用方保留静态目录。
                _logger.LogWarning(
                    ex,
                    "远端模型目录拉取失败，尝试下一个能力: {ProviderId} {Capability}",
                    Id,
                    LlmCapabilityMap.ToConfigKey(capability));
            }
        }

        _logger.LogDebug("Provider 未提供非空远端模型目录: {ProviderId}", Id);
        return null;
    }

    private async Task<List<ModelConfig>> EnrichAllAsync(
        List<ModelConfig> models,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < models.Count; i++)
        {
            models[i] = await _capabilityManager
                .TryEnrichIfEnabledAsync(models[i], cancellationToken)
                .ConfigureAwait(false);
        }

        return models;
    }

    /// <summary>
    /// 把配置界面回填的键值合并进配置副本。
    /// <para>能力清单在落盘前逐个校验名字并规范化为配置键（小驼峰）：写错名字立刻抛，
    /// 而不是静默变成一个"没有该能力"的 Provider（决议 19）。</para>
    /// </summary>
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

        if (TryGetCapabilities(values, "Capabilities", out var capabilities))
            config.Capabilities = capabilities;

        if (TryGetBool(values, "ListModels", out var listModels))
            config.ListModels = listModels;
    }

    /// <summary>
    /// 读取能力清单：接受字符串集合、<see cref="JsonElement"/> 数组或逗号分隔字符串；
    /// 显式传 null / 空集合表示「按协议缺省」。
    /// </summary>
    private static bool TryGetCapabilities(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out List<string>? result)
    {
        result = null;
        if (!values.TryGetValue(key, out var raw))
            return false;

        if (raw is null)
            return true;

        IEnumerable<string?> candidates = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(x => x.ToString()),
            IEnumerable<string> strings => strings,
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Enumerable.Repeat<string?>(raw.ToString(), 1)
        };

        var normalized = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (!LlmCapabilityMap.TryParseConfigKey(candidate, out var kind))
            {
                throw new ArgumentException(
                    $"未识别的能力名 '{candidate}'。合法值："
                    + string.Join(", ", LlmCapabilityMap.ConfigKeys),
                    key);
            }

            var canonical = LlmCapabilityMap.ToConfigKey(kind);
            if (!normalized.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                normalized.Add(canonical);
        }

        result = normalized;
        return true;
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

    /// <summary>
    /// 配置硬编码字段克隆清单之一：<b>必须与 <c>LoadConfigAsync</c> / <c>ApplyValues</c> /
    /// <c>ProviderManager.CloneConfig</c> 保持同一组字段</b>。
    /// </summary>
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
            Timeout = config.Timeout,
            MaxRetries = config.MaxRetries,
            RetryBaseDelayMs = config.RetryBaseDelayMs,
            RetryMaxDelayMs = config.RetryMaxDelayMs,
            RetryTotalBudgetMs = config.RetryTotalBudgetMs,
            Capabilities = config.Capabilities is null ? null : new List<string>(config.Capabilities),
            ListModels = config.ListModels,
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
