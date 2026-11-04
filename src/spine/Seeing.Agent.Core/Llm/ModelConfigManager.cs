using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Llm;
using System.Threading.Channels;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型配置管理器实现 - 负责模型配置的查询、索引和持久化
/// </summary>
public class ModelConfigManager : IModelConfigManager, IDisposable, IAsyncDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<ModelConfigManager> _logger;
    private readonly IServiceProvider? _services;
    private readonly Channel<RefreshRequest> _refreshQueue = Channel.CreateUnbounded<RefreshRequest>();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _refreshWorker;
    private readonly object _cacheLock = new();
    private readonly object _disposeLock = new();
    private long _refreshVersion;
    private long _latestFullRefreshVersion;
    private readonly Dictionary<string, long> _latestProviderRefreshVersions =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _refreshShutdown;

    // 原始索引缓存：Provider 聚合结果，未应用任何用户覆盖（ModelOverrides 的 diff 基线）。
    private IReadOnlyDictionary<string, ModelConfig> _rawCache = new Dictionary<string, ModelConfig>();
    // 模型索引缓存：_rawCache 应用用户覆盖（含补录）后的最终形态，供所有查询消费。
    private IReadOnlyDictionary<string, ModelConfig> _modelCache = new Dictionary<string, ModelConfig>();
    // 启用视图缓存：_modelCache 按 ModelSelection 白名单过滤后的子集。
    private IReadOnlyDictionary<string, ModelConfig> _enabledCache = new Dictionary<string, ModelConfig>();
    private Lazy<Dictionary<string, Dictionary<string, ModelConfig>>> _providerIndex =
        new(() => new Dictionary<string, Dictionary<string, ModelConfig>>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    private Lazy<Dictionary<string, Dictionary<string, ModelConfig>>> _catalogProviderIndex =
        new(() => new Dictionary<string, Dictionary<string, ModelConfig>>(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly List<(long Version, TaskCompletionSource Completion)> _refreshWaiters = [];

    /// <summary>
    /// Providers 节持久化级别（<see cref="ConfigScope.UserOnly"/>）。
    /// </summary>
    public const ConfigLevel ModelStoreLevel = ConfigLevel.User;

    private static readonly TimeSpan DefaultExtensionModelsLoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 单个扩展 Provider 模型目录加载的超时上限（默认 15s）。
    /// 用于防止不可达的 Provider 让单线程刷新队列无界阻塞。
    /// </summary>
    internal TimeSpan ExtensionModelsLoadTimeout { get; set; } = DefaultExtensionModelsLoadTimeout;

    /// <summary>模型配置变更事件</summary>
    public event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged;

    /// <summary>
    /// 初始化模型目录管理器：种子加载配置模型、监听注册表变更并启动单线程刷新队列。
    /// </summary>
    /// <param name="services">
    /// 供 <see cref="TryResolveDefault{TClient}"/> 在调用期惰性解析 <see cref="ILlmRouter"/>。
    /// <para>Router 依赖 <see cref="IModelConfigManager"/>，构造期直接注入会成环，故走服务提供程序延迟取；
    /// 未提供时 TryResolveDefault 给出"未注册 ILlmRouter"的可读 reason。</para>
    /// </param>
    public ModelConfigManager(
        UnifiedConfigManager configManager,
        IProviderRegistry registry,
        ILogger<ModelConfigManager> logger,
        IServiceProvider? services = null)
    {
        _configManager = configManager;
        _registry = registry;
        _logger = logger;
        _services = services;

        // 监听 Provider 注册表变更（配置变更由 ModelReloadHandler 经编排器触发）
        _registry.ProvidersChanged += OnProvidersChanged;

        // 初始目录只同步读取 Providers[*].Models，不依赖注册表装配时序。
        SeedConfiguredModels();
        _refreshWorker = ProcessRefreshQueueAsync(_disposeCts.Token);
        EnqueueRefresh("initial");

        _logger.LogInformation("ModelConfigManager 已初始化，加载 {Count} 个模型", _modelCache.Count);
    }

    #region 查询

    /// <summary>获取所有「已启用」模型配置（按 <c>ModelSelection</c> 白名单过滤后的子集）</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModels() => _enabledCache;

    /// <summary>获取全部模型配置（不经启用清单过滤）</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetCatalogModels() => _modelCache;

    /// <summary>获取指定模型配置</summary>
    public ModelConfig? GetModel(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return null;

        // 1. 直接匹配目录键
        if (_modelCache.TryGetValue(modelId, out var config))
            return config;

        var providers = _registry.GetProviders().Keys;
        var (providerId, apiModelId) = ModelRef.Parse(modelId, providers);

        // 2. 已知 Provider 前缀：provider/apiModelId
        if (!string.IsNullOrEmpty(providerId))
        {
            var key = ModelRef.Format(providerId, apiModelId);
            if (_modelCache.TryGetValue(key, out config))
                return config;
        }

        // 3. 按 ModelConfig.Id（可含 /）+ 可选 Provider 匹配
        foreach (var (key, cfg) in _modelCache)
        {
            if (!string.Equals(cfg.Id, apiModelId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cfg.Id, modelId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(providerId)
                && !string.Equals(cfg.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                continue;

            return cfg;
        }

        // 4. 兼容：裸 modelId 拼到各 Provider 下
        if (string.IsNullOrEmpty(providerId))
        {
            foreach (var pid in providers)
            {
                if (_modelCache.TryGetValue(ModelRef.Format(pid, modelId), out config))
                    return config;
            }
        }

        return null;
    }

    /// <summary>
    /// 取指定能力的全局默认模型引用：<b>严格查表</b>，未配置返回 null，
    /// 不回落到其他能力、不查目录猜测（决议 13 / spec §13 原则 1）。
    /// </summary>
    public string? GetDefaultModel(LlmCapabilityKind capability)
    {
        if (capability == LlmCapabilityKind.None)
            return null;

        foreach (var (configuredKey, value) in GetRawDefaultModels())
        {
            // 配置键的唯一出处是 LlmCapabilityMap：反向解析后比对语义位，不手拼字符串
            if (!LlmCapabilityMap.TryParseConfigKey(configuredKey, out var kind) || kind != capability)
                continue;

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    /// <summary>获取全部已配置的能力默认值（只含已配置项，未登记的能力键被忽略）。</summary>
    public IReadOnlyDictionary<LlmCapabilityKind, string> GetAllDefaultModels()
    {
        var result = new Dictionary<LlmCapabilityKind, string>();
        foreach (var (configuredKey, value) in GetRawDefaultModels())
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!LlmCapabilityMap.TryParseConfigKey(configuredKey, out var kind))
            {
                _logger.LogWarning(
                    "DefaultModels 含未登记的能力键 {Key}，已忽略。合法键：{Valid}",
                    configuredKey,
                    string.Join(", ", RoutableConfigKeys()));
                continue;
            }

            result[kind] = value.Trim();
        }

        return result;
    }

    /// <summary>获取指定 Provider 下「已启用」的模型列表</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return new Dictionary<string, ModelConfig>();

        var index = Volatile.Read(ref _providerIndex).Value;
        return index.TryGetValue(providerId, out var models)
            ? models
            : new Dictionary<string, ModelConfig>();
    }

    /// <summary>获取指定 Provider 下的全部模型列表（不经启用清单过滤）</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetCatalogModelsByProvider(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return new Dictionary<string, ModelConfig>();

        var index = Volatile.Read(ref _catalogProviderIndex).Value;
        return index.TryGetValue(providerId, out var models)
            ? models
            : new Dictionary<string, ModelConfig>();
    }

    /// <summary>获取指定 Provider 的启用模型 ID 列表</summary>
    public IReadOnlyList<string> GetEnabledModelIds(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return [];

        var selection = GetModelSelection();
        if (selection.Providers.TryGetValue(providerId, out var ids) && ids is { Count: > 0 })
            return ids.ToList();

        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config)
        => ModelTypeRules.GetEffectiveTypes(config);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
        ModelType type = ModelType.Text,
        string? providerId = null)
        => ModelTypeRules.FilterByType(GetModels(), type, providerId);

    /// <inheritdoc />
    public bool CanSetAsDefaultModel(string modelId, LlmCapabilityKind capability)
    {
        if (string.IsNullOrWhiteSpace(modelId) || capability == LlmCapabilityKind.None)
            return false;

        var config = GetModel(modelId);
        return config is not null && ModelCoversCapability(config, capability);
    }

    /// <inheritdoc />
    public Task RefreshCatalogAsync(
        string? providerId = null,
        CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
        if (normalized is not null
            && _registry.GetProvider(normalized) is null
            && !GetUserProviders().ContainsKey(normalized))
        {
            return Task.FromException(new ArgumentException(
                $"Provider '{normalized}' 未找到。",
                nameof(providerId)));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = normalized is null ? "manual-full" : "manual-provider";
        EnqueueRefresh(source, normalized, completion);
        return completion.Task.WaitAsync(ct);
    }

    #endregion

    #region 持久化

    /// <summary>添加模型配置</summary>
    public async Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        var providerId = RequireWritableProvider(config.Provider);
        if (!await SaveProviderModelsAsync(providerId, models =>
            {
                models[NormalizeModelId(modelId, providerId)] = config;
                return true;
            }, level, ct))
            return;

        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ModelId = modelId,
            ChangeType = ModelConfigChangeType.Added,
            NewConfig = config
        });

        _logger.LogInformation("已添加模型配置: {ModelId}", modelId);
    }

    /// <summary>更新模型配置</summary>
    public async Task UpdateModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        var oldConfig = GetModel(modelId);

        var providerId = ResolveWritableProvider(modelId, oldConfig, config);
        if (string.IsNullOrEmpty(providerId))
            return;

        config.Provider = providerId;
        if (!await SaveProviderModelsAsync(providerId, models =>
            {
                models[NormalizeModelId(modelId, providerId)] = config;
                return true;
            }, level, ct))
            return;

        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ModelId = modelId,
            ChangeType = ModelConfigChangeType.Updated,
            OldConfig = oldConfig,
            NewConfig = config
        });

        _logger.LogInformation("已更新模型配置: {ModelId}", modelId);
    }

    /// <summary>删除模型配置</summary>
    public async Task DeleteModelAsync(
        string modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        var oldConfig = GetModel(modelId);

        var providerId = ResolveWritableProvider(modelId, oldConfig);
        if (!await SaveProviderModelsAsync(providerId, models => models.Remove(NormalizeModelId(modelId, providerId)), level, ct))
        {
            _logger.LogDebug("模型不存在或 Provider 不可写，跳过删除: {ModelId}", modelId);
            return;
        }

        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ModelId = modelId,
            ChangeType = ModelConfigChangeType.Deleted,
            OldConfig = oldConfig
        });

        _logger.LogInformation("已删除模型配置: {ModelId}", modelId);
    }

    /// <summary>批量保存模型配置</summary>
    public async Task SaveModelsAsync(
        string providerId,
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        providerId = RequireWritableProvider(providerId);
        if (string.IsNullOrEmpty(providerId))
            return;

        foreach (var config in models.Values)
            config.Provider = providerId;

        await SaveProviderModelsAsync(
            providerId,
            destination =>
            {
                destination.Clear();
                foreach (var (modelId, config) in models)
                    destination[NormalizeModelId(modelId, providerId)] = config;
                return true;
            },
            level,
            ct);

        _logger.LogInformation("已保存 {Count} 个模型配置", models.Count);
    }

    /// <summary>
    /// 设置某能力的默认模型。写时校验（决议 14 / 21）：引用必须带 <c>provider/</c> 前缀、
    /// 模型存在、且模型有效类型映射到该能力；任一不满足抛 <see cref="ArgumentException"/>。
    /// <para><paramref name="modelId"/> 为 null/空白 = 清除该能力的默认值（清除不做存在性校验）。</para>
    /// </summary>
    public async Task SetDefaultModelAsync(
        LlmCapabilityKind capability,
        string? modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        var configKey = RequireRoutableCapability(capability);

        // 清除路径：只移除该能力槽位，不校验模型（决议 14 的校验对象是"写入的值"）。
        if (string.IsNullOrWhiteSpace(modelId))
        {
            var cleared = await LoadDefaultModelsAtLevelAsync(level, ct).ConfigureAwait(false);
            if (!cleared.Remove(configKey))
                return;

            await SaveDefaultModelsAsync(cleared, level, configKey, ct).ConfigureAwait(false);
            _logger.LogInformation("已清除能力 {Capability} 的默认模型", configKey);
            return;
        }

        var reference = modelId.Trim();

        // 决议 21：必须带 provider/ 前缀。裸 ID 在多 Provider 同名时歧义无法可靠消解。
        if (!reference.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"默认模型 '{reference}' 必须写成 provider/模型 形式（例如 'openai/{reference}'）。",
                nameof(modelId));
        }

        var config = GetModel(reference);
        if (config is null)
        {
            throw new ArgumentException(
                $"模型 '{reference}' 不在模型目录中，不能作为能力 '{configKey}' 的默认模型。",
                nameof(modelId));
        }

        if (!ModelCoversCapability(config, capability))
        {
            throw new ArgumentException(
                $"模型 '{reference}' 不具备 '{configKey}' 能力（声明类型：{DescribeCapabilities(config)}）。",
                nameof(modelId));
        }

        var models = await LoadDefaultModelsAtLevelAsync(level, ct).ConfigureAwait(false);
        models[configKey] = reference;
        await SaveDefaultModelsAsync(models, level, configKey, ct).ConfigureAwait(false);

        _logger.LogInformation("已设置能力 {Capability} 的默认模型: {ModelId}", configKey, reference);
    }

    /// <summary>
    /// 便捷解析：查默认值 → 经 <see cref="ILlmRouter"/> 解析客户端。
    /// <para>三类失败各给可读 reason：未配置 / Router 不可用 / Router 解析失败。</para>
    /// </summary>
    public bool TryResolveDefault<TClient>(
        LlmCapabilityKind capability,
        out TClient? client,
        out string? reason)
        where TClient : class, ILlmCapabilityClient
    {
        client = null;
        var configKey = LlmCapabilityMap.ToConfigKey(capability);

        var modelId = GetDefaultModel(capability);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            reason = $"未配置 '{configKey}' 能力的默认模型（SeeingAgent:DefaultModels:{configKey}）。";
            return false;
        }

        var router = ResolveRouter();
        if (router is null)
        {
            reason = $"未注册 {nameof(ILlmRouter)}，无法解析默认模型 '{modelId}'。";
            return false;
        }

        if (!router.TryResolve(modelId, out client, out var resolveReason))
        {
            client = null;
            reason = $"默认模型 '{modelId}' 解析失败：{resolveReason ?? "未知原因"}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// 校验全部默认值：模型是否仍存在、是否仍具备所声明的能力（决议 23）。
    /// <para><b>只报告，不自动清除</b>：失效项留在配置里等用户修。</para>
    /// </summary>
    public IReadOnlyDictionary<LlmCapabilityKind, string> ValidateDefaultModels()
    {
        var problems = new Dictionary<LlmCapabilityKind, string>();

        foreach (var (configuredKey, value) in GetRawDefaultModels())
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var reference = value.Trim();

            if (!LlmCapabilityMap.TryParseConfigKey(configuredKey, out var kind))
            {
                // 未登记的能力键无法投影到枚举位，用 None 承载这条诊断。
                problems[LlmCapabilityKind.None] =
                    $"DefaultModels 含未登记的能力键 '{configuredKey}'（值 '{reference}'）。" +
                    $"合法键：{string.Join(", ", RoutableConfigKeys())}";
                continue;
            }

            var config = GetModel(reference);
            if (config is null)
            {
                problems[kind] = $"默认模型 '{reference}' 已不在模型目录中（配置已保留，请手动修正）。";
                continue;
            }

            if (!ModelCoversCapability(config, kind))
            {
                problems[kind] =
                    $"默认模型 '{reference}' 已不具备该能力（当前声明类型：{DescribeCapabilities(config)}）。";
            }
        }

        return problems;
    }

    /// <summary>设置指定 Provider 的启用模型清单（allowlist）</summary>
    public async Task SetEnabledModelsAsync(
        string providerId,
        IReadOnlyList<string> modelIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider 不能为空。", nameof(providerId));

        var current = GetModelSelection();
        var providers = current.Providers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null ? new List<string>() : new List<string>(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        if (modelIds is null || modelIds.Count == 0)
        {
            providers.Remove(providerId);
        }
        else
        {
            providers[providerId] = modelIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var updated = new ModelSelectionConfig { Providers = providers };
        await _configManager
            .SaveSectionAsync("ModelSelection", updated, ConfigLevel.User, new[] { providerId }, ct)
            .ConfigureAwait(false);

        // SaveSectionAsync 已同步更新内存缓存；仅重建启用视图，不重拉 Provider。
        RebuildViews();

        _logger.LogInformation(
            "已更新 Provider {ProviderId} 启用模型清单，共 {Count} 个",
            providerId,
            updated.Providers.TryGetValue(providerId, out var list) ? list.Count : 0);
    }

    #endregion

    #region 元数据覆盖（ModelOverrides）

    /// <inheritdoc />
    public ModelOverride? GetModelOverride(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        var overrides = GetModelOverrides();
        return overrides.TryGetValue(ModelRef.Format(providerId, modelId), out var patch) ? patch : null;
    }

    /// <summary>
    /// 获取指定模型的无覆盖基线配置（未应用任何用户覆盖的原始形态）；补录模型返回 null。
    /// </summary>
    internal ModelConfig? GetModelBaseline(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return null;

        var raw = Volatile.Read(ref _rawCache);
        return raw.TryGetValue(ModelRef.Format(providerId, modelId), out var config) ? config : null;
    }

    /// <inheritdoc />
    public async Task UpsertModelOverrideAsync(
        string providerId,
        string modelId,
        ModelOverride desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        providerId = RequireKnownProvider(providerId);
        modelId = RequireModelId(modelId);

        var key = ModelRef.Format(providerId, modelId);
        var baseline = GetModelBaseline(providerId, modelId);
        var patch = ModelOverrideApplier.Diff(baseline, desired);

        var overrides = CloneModelOverrides();
        if (patch.IsEmpty && baseline is not null)
        {
            // 与基线无差异且模型本就存在：等价于无覆盖。
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = patch;
        }

        await SaveModelOverridesAsync(overrides, key, ct).ConfigureAwait(false);
        RebuildViews();

        _logger.LogInformation("已更新模型元数据覆盖: {Key}", key);
    }

    /// <inheritdoc />
    public async Task RemoveModelOverrideAsync(
        string providerId,
        string modelId,
        CancellationToken ct = default)
    {
        providerId = RequireKnownProvider(providerId);
        modelId = RequireModelId(modelId);

        var key = ModelRef.Format(providerId, modelId);
        var overrides = CloneModelOverrides();
        if (!overrides.Remove(key))
            return;

        await SaveModelOverridesAsync(overrides, key, ct).ConfigureAwait(false);
        RebuildViews();

        _logger.LogInformation("已移除模型元数据覆盖: {Key}", key);
    }

    private string RequireKnownProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("模型必须指定 Provider。", nameof(providerId));

        if (_registry.GetProvider(providerId) is null
            && !GetUserProviders().ContainsKey(providerId))
        {
            throw new ArgumentException($"Provider '{providerId}' 未注册。", nameof(providerId));
        }

        return providerId;
    }

    private static string RequireModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("模型 ID 不能为空。", nameof(modelId));

        return modelId.Trim();
    }

    private IReadOnlyDictionary<string, ModelOverride> GetModelOverrides()
        => _configManager.GetSection<Dictionary<string, ModelOverride>>("ModelOverrides");

    private Dictionary<string, ModelOverride> CloneModelOverrides()
        => GetModelOverrides().ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.Clone() ?? new ModelOverride(),
            StringComparer.OrdinalIgnoreCase);

    private async Task SaveModelOverridesAsync(
        Dictionary<string, ModelOverride> overrides,
        string changedKey,
        CancellationToken ct)
    {
        await _configManager
            .SaveSectionAsync("ModelOverrides", overrides, ModelStoreLevel, new[] { changedKey }, ct)
            .ConfigureAwait(false);
    }

    #endregion

    #region 私有方法

    private void OnProvidersChanged(object? sender, ProvidersChangedEventArgs e)
    {
        // 无粒度信息（如手工构造的事件）→ 回退全量刷新
        if (e.ChangedProviderIds.Count == 0 && e.RemovedProviderIds.Count == 0)
        {
            EnqueueRefresh("provider-registry");
            return;
        }

        // 新增/替换 → 仅刷新该 Provider；移除 → 仅清理该 Provider 的缓存条目
        foreach (var providerId in e.ChangedProviderIds.Concat(e.RemovedProviderIds).Distinct())
            EnqueueProviderRefresh("provider-registry", providerId);
    }

    internal void EnqueueRefresh(string source)
        => EnqueueRefresh(source, providerId: null, completion: null);

    /// <summary>按 Provider 粒度刷新（供配置变更 / 注册表作用域变更使用）。</summary>
    internal void EnqueueProviderRefresh(string source, string providerId)
        => EnqueueRefresh(source, providerId, completion: null);

    private void EnqueueRefresh(string source, string? providerId, TaskCompletionSource? completion)
    {
        long version;
        lock (_cacheLock)
        {
            if (_disposeCts.IsCancellationRequested)
            {
                completion?.TrySetCanceled(_disposeCts.Token);
                return;
            }

            version = ++_refreshVersion;
            if (providerId is null)
                _latestFullRefreshVersion = version;
            else
                _latestProviderRefreshVersions[providerId] = version;
            if (completion is not null)
                _refreshWaiters.Add((version, completion));
        }

        if (!_refreshQueue.Writer.TryWrite(new RefreshRequest(version, source, providerId)))
        {
            _logger.LogDebug("模型目录刷新队列已关闭，忽略 {Source} 请求", source);
            if (completion is not null)
                FailWaiter(version, new ObjectDisposedException(nameof(ModelConfigManager)));
        }
    }

    private async Task ProcessRefreshQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _refreshQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await RefreshFromProvidersAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            CancelPendingWaiters();
        }
    }

    private async Task RefreshFromProvidersAsync(RefreshRequest request, CancellationToken ct)
    {
        if (request.ProviderId is { } providerId)
        {
            await RefreshSingleProviderAsync(request, providerId, ct).ConfigureAwait(false);
            return;
        }

        var models = new Dictionary<string, ModelConfig>();

        // 配置驱动：用户级 Providers[*].Models；扩展：仍走 ILlmProvider.GetModelsAsync。
        foreach (var (configuredProviderId, providerConfig) in GetUserProviders())
        {
            if (providerConfig.Models is null)
                continue;

            foreach (var (modelId, config) in providerConfig.Models)
                models[ModelRef.Format(configuredProviderId, modelId)] =
                    CloneModelConfig(configuredProviderId, modelId, config);
        }

        var extensionLoads = _registry.GetProviders()
            .Where(pair => _registry.GetOwnerExtensionId(pair.Key) is not null)
            .Select(pair => LoadProviderModelsAsync(pair.Key, pair.Value, ct));
        var extensionModels = await Task.WhenAll(extensionLoads).ConfigureAwait(false);

        foreach (var (extensionProviderId, configurations) in extensionModels)
        {
            foreach (var config in configurations)
            {
                if (string.IsNullOrWhiteSpace(config.Id))
                    continue;

                config.Provider = extensionProviderId;
                models[ModelRef.Format(extensionProviderId, config.Id)] = config;
            }
        }

        if (!TryReplaceCache(request.Version, models, CollectActiveProviderIds()))
        {
            _logger.LogDebug("丢弃过期模型目录刷新 {Version}（当前 {CurrentVersion}）",
                request.Version, Volatile.Read(ref _refreshVersion));
            return;
        }

        CompleteWaitersUpTo(request.Version);
        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ChangeType = ModelConfigChangeType.Updated
        });
    }

    private async Task RefreshSingleProviderAsync(
        RefreshRequest request,
        string providerId,
        CancellationToken ct)
    {
        var slice = await LoadProviderSliceAsync(providerId, ct).ConfigureAwait(false);
        var activeProviderIds = CollectActiveProviderIds();

        lock (_cacheLock)
        {
            if (IsProviderRefreshStaleLocked(request.Version, providerId))
            {
                _logger.LogDebug("丢弃过期单 Provider 刷新 {Version}（当前 {CurrentVersion}）",
                    request.Version, _refreshVersion);
                return;
            }

            // 在原始缓存层合并（不含覆盖/补录），再统一应用覆盖，避免把覆盖结果当成基线。
            var mergedRaw = new Dictionary<string, ModelConfig>(_rawCache);
            RemoveProviderEntries(mergedRaw, providerId);
            foreach (var (key, config) in slice)
                mergedRaw[key] = config;

            ReplaceCacheLocked(mergedRaw);
            PruneInactiveProviderVersionsLocked(activeProviderIds);
        }

        _logger.LogDebug("已刷新 Provider {ProviderId} 模型目录，共 {Count} 个模型", providerId, slice.Count);
        CompleteWaitersUpTo(request.Version);
        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ChangeType = ModelConfigChangeType.Updated
        });
    }

    private async Task<Dictionary<string, ModelConfig>> LoadProviderSliceAsync(
        string providerId,
        CancellationToken ct)
    {
        var models = new Dictionary<string, ModelConfig>();

        if (_registry.GetOwnerExtensionId(providerId) is not null)
        {
            var provider = _registry.GetProvider(providerId);
            if (provider is null)
                return models;

            var loaded = await LoadProviderModelsAsync(providerId, provider, ct).ConfigureAwait(false);
            foreach (var config in loaded.Value)
            {
                if (string.IsNullOrWhiteSpace(config.Id))
                    continue;

                config.Provider = providerId;
                models[ModelRef.Format(providerId, config.Id)] = config;
            }

            return models;
        }

        if (GetUserProviders().TryGetValue(providerId, out var providerConfig)
            && providerConfig.Models is not null)
        {
            foreach (var (modelId, config) in providerConfig.Models)
                models[ModelRef.Format(providerId, modelId)] = CloneModelConfig(providerId, modelId, config);
        }

        return models;
    }

    private static void RemoveProviderEntries(Dictionary<string, ModelConfig> models, string providerId)
    {
        var keys = models
            .Where(pair => string.Equals(pair.Value.Provider, providerId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in keys)
            models.Remove(key);
    }

    private void CompleteWaitersUpTo(long appliedVersion)
    {
        List<TaskCompletionSource> completed;
        lock (_cacheLock)
        {
            completed = [];
            _refreshWaiters.RemoveAll(waiter =>
            {
                if (waiter.Version > appliedVersion)
                    return false;

                completed.Add(waiter.Completion);
                return true;
            });
        }

        foreach (var completion in completed)
            completion.TrySetResult();
    }

    private void FailWaiter(long version, Exception exception)
    {
        TaskCompletionSource? completion = null;
        lock (_cacheLock)
        {
            var index = _refreshWaiters.FindIndex(waiter => waiter.Version == version);
            if (index >= 0)
            {
                completion = _refreshWaiters[index].Completion;
                _refreshWaiters.RemoveAt(index);
            }
        }

        completion?.TrySetException(exception);
    }

    private void CancelPendingWaiters()
    {
        List<TaskCompletionSource> pending;
        lock (_cacheLock)
        {
            pending = _refreshWaiters.Select(waiter => waiter.Completion).ToList();
            _refreshWaiters.Clear();
        }

        foreach (var completion in pending)
            completion.TrySetCanceled();
    }

    private async Task<KeyValuePair<string, IReadOnlyList<ModelConfig>>> LoadProviderModelsAsync(
        string providerId,
        ILlmProvider provider,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ExtensionModelsLoadTimeout);
            var models = await provider.GetModelsAsync(timeoutCts.Token).ConfigureAwait(false);
            return new KeyValuePair<string, IReadOnlyList<ModelConfig>>(providerId, models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "加载 Provider {ProviderId} 模型目录超时（{Timeout}ms），已跳过",
                providerId,
                ExtensionModelsLoadTimeout.TotalMilliseconds);
            return new KeyValuePair<string, IReadOnlyList<ModelConfig>>(providerId, []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Provider {ProviderId} 模型目录失败", providerId);
            return new KeyValuePair<string, IReadOnlyList<ModelConfig>>(providerId, []);
        }
    }

    private void SeedConfiguredModels()
    {
        var models = new Dictionary<string, ModelConfig>();
        foreach (var (providerId, providerConfig) in GetUserProviders())
        {
            if (providerConfig.Models is null)
                continue;

            foreach (var (modelId, config) in providerConfig.Models)
                models[ModelRef.Format(providerId, modelId)] = CloneModelConfig(providerId, modelId, config);
        }

        ReplaceCache(models);
    }

    private static ModelConfig CloneModelConfig(string providerId, string modelId, ModelConfig config)
    {
        var clone = ModelOverrideApplier.Clone(config);
        clone.Provider = providerId;
        if (string.IsNullOrWhiteSpace(clone.Id))
            clone.Id = modelId;
        return clone;
    }

    private void ReplaceCache(IReadOnlyDictionary<string, ModelConfig> models)
    {
        lock (_cacheLock)
        {
            ReplaceCacheLocked(models);
        }

        _logger.LogDebug("模型缓存已刷新，共 {Count} 个模型", models.Count);
    }

    private bool TryReplaceCache(
        long version,
        IReadOnlyDictionary<string, ModelConfig> models,
        IReadOnlySet<string> activeProviderIds)
    {
        lock (_cacheLock)
        {
            // 仅当存在更新的「全量」刷新时才判定过期：按 Provider 粒度的刷新不覆盖其他 Provider，
            // 若据此丢弃在途全量刷新会导致其 Provider 的模型永久丢失（见 ModelCatalogAggregationTests）。
            if (version < _latestFullRefreshVersion)
                return false;

            ReplaceCacheLocked(models);
            PruneInactiveProviderVersionsLocked(activeProviderIds);
        }

        _logger.LogDebug("模型缓存已刷新，共 {Count} 个模型", models.Count);
        return true;
    }

    /// <summary>
    /// 采集当前仍存在的 Provider id 集合（注册表 ∪ 用户级配置）。
    /// 调用方不得持有 <see cref="_cacheLock"/>，以避免与配置/注册表锁形成反向锁序。
    /// </summary>
    private HashSet<string> CollectActiveProviderIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerId in _registry.GetProviders().Keys)
            ids.Add(providerId);
        foreach (var providerId in GetUserProviders().Keys)
            ids.Add(providerId);

        return ids;
    }

    /// <summary>
    /// 裁剪已不存在 Provider 的刷新版本条目，避免长驻进程在 Provider 反复增删后无界增长。
    /// 仅在缓存替换成功后调用，且调用方须持有 <see cref="_cacheLock"/>。
    /// <para>
    /// 语义安全：被裁剪条目的 Provider 已不在注册表/配置中，其可能存在的在途刷新即使应用也只会
    /// 移除残留缓存条目；对仍存在的 Provider，其在途判定的作用域版本语义保持不变。
    /// </para>
    /// </summary>
    private void PruneInactiveProviderVersionsLocked(IReadOnlySet<string> activeProviderIds)
    {
        if (_latestProviderRefreshVersions.Count == 0)
            return;

        List<string>? inactive = null;
        foreach (var providerId in _latestProviderRefreshVersions.Keys)
        {
            if (!activeProviderIds.Contains(providerId))
                (inactive ??= []).Add(providerId);
        }

        if (inactive is null)
            return;

        foreach (var providerId in inactive)
            _latestProviderRefreshVersions.Remove(providerId);
    }

    /// <summary>
    /// 判定按 Provider 粒度的刷新是否已过期：存在更新的全量刷新，或针对同一 Provider 的更新刷新时，
    /// 该结果不再应用到缓存（调用方须持有 <see cref="_cacheLock"/>）。
    /// </summary>
    private bool IsProviderRefreshStaleLocked(long version, string providerId)
    {
        if (version < _latestFullRefreshVersion)
            return true;

        return _latestProviderRefreshVersions.TryGetValue(providerId, out var latestProviderVersion)
            && version < latestProviderVersion;
    }

    /// <summary>
    /// 替换原始缓存并据当前 <c>ModelOverrides</c> 计算最终视图（调用方须持有 <see cref="_cacheLock"/>）。
    /// </summary>
    private void ReplaceCacheLocked(IReadOnlyDictionary<string, ModelConfig> raw)
        => ReplaceCacheLocked(raw, ModelOverrideApplier.ApplyAll(raw, GetModelOverrides()));

    /// <summary>
    /// 以给定的原始/最终缓存替换（调用方须持有 <see cref="_cacheLock"/>）。
    /// </summary>
    private void ReplaceCacheLocked(
        IReadOnlyDictionary<string, ModelConfig> raw,
        IReadOnlyDictionary<string, ModelConfig> final)
    {
        Volatile.Write(ref _rawCache, raw);
        Volatile.Write(ref _modelCache, final);
        _catalogProviderIndex = new Lazy<Dictionary<string, Dictionary<string, ModelConfig>>>(
            () => BuildProviderIndex(final),
            LazyThreadSafetyMode.ExecutionAndPublication);
        RebuildEnabledViewLocked();
    }

    /// <summary>
    /// 按 <c>ModelSelection</c> 白名单重建启用视图与启用 Provider 索引。
    /// 调用方须持有 <see cref="_cacheLock"/>。
    /// </summary>
    private void RebuildEnabledViewLocked()
    {
        var selection = GetModelSelection();
        var full = Volatile.Read(ref _modelCache);
        var enabled = new Dictionary<string, ModelConfig>();

        foreach (var (key, config) in full)
        {
            if (IsModelEnabled(selection, config))
                enabled[key] = config;
        }

        Volatile.Write(ref _enabledCache, enabled);
        _providerIndex = new Lazy<Dictionary<string, Dictionary<string, ModelConfig>>>(
            () => BuildProviderIndex(enabled),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static bool IsModelEnabled(ModelSelectionConfig selection, ModelConfig config)
    {
        if (string.IsNullOrEmpty(config.Provider))
            return false;

        if (!selection.Providers.TryGetValue(config.Provider, out var ids) || ids is null)
            return false;

        foreach (var id in ids)
        {
            if (string.Equals(id, config.Id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private ModelSelectionConfig GetModelSelection()
        => _configManager.GetSection<ModelSelectionConfig>("ModelSelection")
           ?? new ModelSelectionConfig();

    /// <summary>
    /// 轻量重建视图：从原始缓存重走「应用覆盖 + 白名单」，不重拉 Provider。
    /// 供 <c>ModelSelection</c> / <c>ModelOverrides</c> 节变更热重载使用。
    /// </summary>
    internal void RebuildViews()
    {
        lock (_cacheLock)
        {
            var raw = Volatile.Read(ref _rawCache);
            ReplaceCacheLocked(raw, ModelOverrideApplier.ApplyAll(raw, GetModelOverrides()));
        }

        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ChangeType = ModelConfigChangeType.Updated
        });
    }

    /// <summary>
    /// 读取当前合并视图的 <c>DefaultModels</c> 原始字典（永不为 null；键为配置端小驼峰字符串）。
    /// </summary>
    private KeyValuePair<string, string>[] GetRawDefaultModels()
    {
        var defaults = _configManager.SeeingAgent.DefaultModels;
        return defaults is null
            ? []
            : defaults.Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                      .Select(pair => new KeyValuePair<string, string>(pair.Key.Trim(), pair.Value))
                      .ToArray();
    }

    /// <summary>读取指定级别已持久化的 DefaultModels（用于读—改—写，避免抹掉另一级的条目）。</summary>
    private async Task<Dictionary<string, string>> LoadDefaultModelsAtLevelAsync(
        ConfigLevel level,
        CancellationToken ct)
    {
        var atLevel = await _configManager
            .GetSectionAtLevelAsync<Dictionary<string, string>>("DefaultModels", level, ct)
            .ConfigureAwait(false);

        return atLevel is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(atLevel, StringComparer.OrdinalIgnoreCase);
    }

    private Task SaveDefaultModelsAsync(
        Dictionary<string, string> models,
        ConfigLevel level,
        string changedKey,
        CancellationToken ct)
        => _configManager.SaveSectionAsync(
            "DefaultModels",
            models,
            level,
            new[] { changedKey },
            ct);

    /// <summary>
    /// 校验能力位并返回其配置键。<see cref="LlmCapabilityKind.None"/> 与复合位集不作为默认值槽位。
    /// </summary>
    private static string RequireRoutableCapability(LlmCapabilityKind capability)
    {
        if (capability == LlmCapabilityKind.None || (capability & (capability - 1)) != 0)
        {
            throw new ArgumentException(
                $"能力必须是单一已登记位，收到 '{capability}'。",
                nameof(capability));
        }

        return LlmCapabilityMap.ToConfigKey(capability);
    }

    /// <summary>全部可作为 DefaultModels 键的配置名（诊断文案用，出处仍是 LlmCapabilityMap）。</summary>
    private static IEnumerable<string> RoutableConfigKeys() =>
        Enum.GetValues<LlmCapabilityKind>()
            .Where(kind => kind != LlmCapabilityKind.None)
            .Select(LlmCapabilityMap.ToConfigKey);

    /// <summary>
    /// 模型有效类型经 <see cref="LlmCapabilityMap.FromModelType"/> 投影后是否覆盖所需能力
    /// （复合位要求逐位覆盖）。取代旧的 <c>ModelType.Text</c> 硬编码判定。
    /// </summary>
    private bool ModelCoversCapability(ModelConfig config, LlmCapabilityKind capability)
    {
        var modelKinds = GetEffectiveTypes(config)
            .SelectMany(LlmCapabilityMap.FromModelType)
            .Where(kind => kind != LlmCapabilityKind.None)
            .ToHashSet();

        foreach (var required in SingleCapabilityBits(capability))
        {
            if (!modelKinds.Contains(required))
                return false;
        }

        return true;
    }

    /// <summary>能力位集拆成单值位序列（枚举是唯一出处，不手拼名字）。</summary>
    private static IEnumerable<LlmCapabilityKind> SingleCapabilityBits(LlmCapabilityKind capability)
    {
        foreach (var kind in Enum.GetValues<LlmCapabilityKind>())
        {
            if (kind != LlmCapabilityKind.None &&
                (kind & (kind - 1)) == 0 &&
                capability.HasFlag(kind))
            {
                yield return kind;
            }
        }
    }

    /// <summary>诊断文案：模型有效类型对应到的能力配置键集合。</summary>
    private string DescribeCapabilities(ModelConfig config)
        => string.Join(
            ", ",
            GetEffectiveTypes(config)
                .SelectMany(LlmCapabilityMap.FromModelType)
                .Select(LlmCapabilityMap.ToConfigKey)
                .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 惰性取得 <see cref="ILlmRouter"/>：Router 依赖 <see cref="IModelConfigManager"/>，
    /// 构造期直接注入会形成 DI 环，故经 <see cref="IServiceProvider"/> 在调用期解析。
    /// </summary>
    private ILlmRouter? ResolveRouter() => _services?.GetService<ILlmRouter>();

    private string RequireWritableProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("模型必须指定 Provider。", nameof(providerId));

        if (_registry.GetProvider(providerId) is null)
            throw new ArgumentException($"Provider '{providerId}' 未注册。", nameof(providerId));

        if (_registry.GetOwnerExtensionId(providerId) is not null)
        {
            _logger.LogWarning("扩展 Provider 不支持持久化模型: {ProviderId}", providerId);
            return string.Empty;
        }

        if (!_configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers").ContainsKey(providerId))
            throw new ArgumentException($"Provider '{providerId}' 不是可配置 Provider。", nameof(providerId));

        return providerId;
    }

    private string ResolveWritableProvider(string modelId, ModelConfig? existing, ModelConfig? replacement = null)
    {
        var configuredProvider = FindCatalogProviderId(modelId, existing) ?? existing?.Provider;
        if (string.IsNullOrWhiteSpace(configuredProvider))
            configuredProvider = replacement?.Provider;

        if (string.IsNullOrWhiteSpace(configuredProvider))
        {
            var (providerId, _) = ModelRef.Parse(modelId, _registry.GetProviders().Keys);
            configuredProvider = providerId;
        }

        return RequireWritableProvider(configuredProvider ?? string.Empty);
    }

    private string? FindCatalogProviderId(string modelId, ModelConfig? existing)
    {
        if (existing is null)
            return null;

        var providerIds = _registry.GetProviders().Keys;
        var cache = Volatile.Read(ref _modelCache);
        if (cache.TryGetValue(modelId, out var direct) && ReferenceEquals(direct, existing))
            return ModelRef.Parse(modelId, providerIds).ProviderId;

        foreach (var (key, config) in cache)
        {
            if (ReferenceEquals(config, existing))
                return ModelRef.Parse(key, providerIds).ProviderId;
        }

        return null;
    }

    private async Task<bool> SaveProviderModelsAsync(
        string providerId,
        Func<Dictionary<string, ModelConfig>, bool> update,
        ConfigLevel level,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(providerId))
            return false;

        if (!_configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers").TryGetValue(providerId, out var existingProvider))
            return false;

        if (level != ModelStoreLevel)
        {
            _logger.LogDebug(
                "Providers 为 UserOnly，忽略请求级 {RequestedLevel}（Provider={ProviderId}）",
                level,
                providerId);
        }

        var providers = GetUserProviders().ToDictionary(
            pair => pair.Key,
            pair => CloneProviderConfig(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        var provider = providers.TryGetValue(providerId, out var atUser)
            ? CloneProviderConfig(atUser)
            : CloneProviderConfig(existingProvider);
        provider.Id = providerId;

        var models = new Dictionary<string, ModelConfig>(provider.Models ?? []);
        if (!update(models))
            return false;

        provider.Models = models;
        providers[providerId] = provider;

        await _configManager
            .SaveSectionAsync("Providers", providers, ModelStoreLevel, new[] { providerId }, ct)
            .ConfigureAwait(false);

        // 写操作已知目标 Provider：仅按 providerId 增量刷新该 slice，避免无条件全量刷新
        // （全量分支会对无关的扩展 owner provider 逐个 await GetModelsAsync，拖慢即时生效）。
        EnqueueRefresh("configuration", providerId, completion: null);
        return true;
    }

    private IReadOnlyDictionary<string, ProviderConfig> GetUserProviders()
        => _configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");

    private static ProviderConfig CloneProviderConfig(ProviderConfig config)
        => new()
        {
            Id = config.Id,
            Type = config.Type,
            Name = config.Name,
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey,
            Proxy = config.Proxy,
            UseProxy = config.UseProxy,
            Capabilities = config.Capabilities is null ? null : new List<string>(config.Capabilities),
            ListModels = config.ListModels,
            Timeout = config.Timeout,
            MaxRetries = config.MaxRetries,
            RetryBaseDelayMs = config.RetryBaseDelayMs,
            RetryMaxDelayMs = config.RetryMaxDelayMs,
            RetryTotalBudgetMs = config.RetryTotalBudgetMs,
            Models = config.Models is null ? null : new Dictionary<string, ModelConfig>(config.Models),
            Options = config.Options is null ? null : new Dictionary<string, object>(config.Options),
            Headers = config.Headers is null ? null : new Dictionary<string, string>(config.Headers)
        };

    private static string NormalizeModelId(string modelId, string providerId)
    {
        var (referencedProvider, apiModelId) = ModelRef.Parse(modelId, [providerId]);
        return referencedProvider is null ? modelId : apiModelId;
    }

    /// <summary>构建 Provider 索引</summary>
    private static Dictionary<string, Dictionary<string, ModelConfig>> BuildProviderIndex(
        IReadOnlyDictionary<string, ModelConfig> modelCache)
    {
        var index = new Dictionary<string, Dictionary<string, ModelConfig>>();

        foreach (var (key, config) in modelCache)
        {
            var providerId = config.Provider;
            if (string.IsNullOrEmpty(providerId)) continue;

            if (!index.TryGetValue(providerId, out var providerModels))
            {
                providerModels = new Dictionary<string, ModelConfig>();
                index[providerId] = providerModels;
            }
            providerModels[key] = config;
        }

        return index;
    }

    #endregion

    /// <summary>
    /// 释放资源：关闭模型目录刷新工作器。
    /// </summary>
    public void Dispose()
    {
        _ = ShutdownRefreshWorkerAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放资源：等待刷新工作器完全关闭。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await ShutdownRefreshWorkerAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task ShutdownRefreshWorkerAsync()
    {
        lock (_disposeLock)
        {
            if (_refreshShutdown is not null)
                return _refreshShutdown;

            _refreshShutdown = AwaitRefreshWorkerShutdownAsync();
            return _refreshShutdown;
        }
    }

    private async Task AwaitRefreshWorkerShutdownAsync()
    {
        _registry.ProvidersChanged -= OnProvidersChanged;
        _refreshQueue.Writer.TryComplete();
        _disposeCts.Cancel();

        try
        {
            await _refreshWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭模型目录刷新工作器时发生错误");
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }

    private sealed record RefreshRequest(long Version, string Source, string? ProviderId = null);
}
