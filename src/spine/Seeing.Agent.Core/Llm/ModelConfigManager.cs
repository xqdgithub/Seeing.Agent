using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using System.Threading.Channels;
using Seeing.Agent.Llm;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型配置管理器实现 - 负责模型配置的查询、索引和持久化
/// </summary>
public class ModelConfigManager : IModelConfigManager, IDisposable, IAsyncDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<ModelConfigManager> _logger;
    private readonly Channel<RefreshRequest> _refreshQueue = Channel.CreateUnbounded<RefreshRequest>();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _refreshWorker;
    private readonly object _cacheLock = new();
    private readonly object _disposeLock = new();
    private long _refreshVersion;
    private Task? _refreshShutdown;

    // 模型索引缓存
    private IReadOnlyDictionary<string, ModelConfig> _modelCache = new Dictionary<string, ModelConfig>();
    private Lazy<Dictionary<string, Dictionary<string, ModelConfig>>> _providerIndex =
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

    public ModelConfigManager(
        UnifiedConfigManager configManager,
        IProviderRegistry registry,
        ILogger<ModelConfigManager> logger)
    {
        _configManager = configManager;
        _registry = registry;
        _logger = logger;

        // 监听 Provider 注册表变更（配置变更由 ModelReloadHandler 经编排器触发）
        _registry.ProvidersChanged += OnProvidersChanged;

        // 初始目录只同步读取 Providers[*].Models，不依赖注册表装配时序。
        SeedConfiguredModels();
        _refreshWorker = ProcessRefreshQueueAsync(_disposeCts.Token);
        EnqueueRefresh("initial");

        _logger.LogInformation("ModelConfigManager 已初始化，加载 {Count} 个模型", _modelCache.Count);
    }

    #region 查询

    /// <summary>获取所有模型配置</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModels() => _modelCache;

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

    /// <summary>获取默认模型 ID</summary>
    public string? GetDefaultModel() => _configManager.SeeingAgent.DefaultModel;

    /// <summary>获取指定 Provider 下的模型列表</summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
            return new Dictionary<string, ModelConfig>();

        var index = Volatile.Read(ref _providerIndex).Value;
        return index.TryGetValue(providerId, out var models)
            ? models
            : new Dictionary<string, ModelConfig>();
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
    public bool CanSetAsDefaultModel(string modelId)
    {
        var config = GetModel(modelId);
        return config is not null && GetEffectiveTypes(config).Contains(ModelType.Text);
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

    /// <summary>设置默认模型</summary>
    public async Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(modelId) && !CanSetAsDefaultModel(modelId))
            throw new InvalidOperationException($"模型 '{modelId}' 不是 Text 类型，不能设为默认对话模型。");

        await _configManager.SaveSectionAsync("DefaultModel", modelId ?? (string)null!, ModelStoreLevel, ct);

        _logger.LogInformation("已设置默认模型: {ModelId}", modelId ?? "(空)");
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

        if (!TryReplaceCache(request.Version, models))
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

        lock (_cacheLock)
        {
            if (request.Version != _refreshVersion)
            {
                _logger.LogDebug("丢弃过期单 Provider 刷新 {Version}（当前 {CurrentVersion}）",
                    request.Version, _refreshVersion);
                return;
            }

            var merged = new Dictionary<string, ModelConfig>(_modelCache);
            RemoveProviderEntries(merged, providerId);
            foreach (var (key, config) in slice)
                merged[key] = config;

            ReplaceCacheLocked(merged);
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
        => new()
        {
            Id = string.IsNullOrWhiteSpace(config.Id) ? modelId : config.Id,
            Name = config.Name,
            Provider = providerId,
            Types = config.Types is null ? new List<ModelType>() : new List<ModelType>(config.Types),
            Modalities = config.Modalities,
            Limit = config.Limit,
            Options = config.Options,
            Pricing = config.Pricing,
            Metadata = config.Metadata is null
                ? null
                : new Dictionary<string, object?>(config.Metadata)
        };

    private void ReplaceCache(IReadOnlyDictionary<string, ModelConfig> models)
    {
        lock (_cacheLock)
        {
            ReplaceCacheLocked(models);
        }

        _logger.LogDebug("模型缓存已刷新，共 {Count} 个模型", models.Count);
    }

    private bool TryReplaceCache(long version, IReadOnlyDictionary<string, ModelConfig> models)
    {
        lock (_cacheLock)
        {
            if (version != _refreshVersion)
                return false;

            ReplaceCacheLocked(models);
        }

        _logger.LogDebug("模型缓存已刷新，共 {Count} 个模型", models.Count);
        return true;
    }

    private void ReplaceCacheLocked(IReadOnlyDictionary<string, ModelConfig> models)
    {
        Volatile.Write(ref _modelCache, models);
        _providerIndex = new Lazy<Dictionary<string, Dictionary<string, ModelConfig>>>(
            () => BuildProviderIndex(models),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

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

    public void Dispose()
    {
        _ = ShutdownRefreshWorkerAsync();
        GC.SuppressFinalize(this);
    }

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
