using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Threading.Channels;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Llm;

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

    /// <summary>
    /// Providers 节持久化级别（<see cref="ConfigScope.UserOnly"/>）。
    /// </summary>
    public const ConfigLevel ModelStoreLevel = ConfigLevel.User;

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

        // 监听配置变更
        _configManager.ConfigChanged += OnConfigChanged;
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
        if (!await SaveProviderModelsAsync(providerId, models => models[NormalizeModelId(modelId, providerId)] = config, level, ct))
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
        if (!await SaveProviderModelsAsync(providerId, models => models[NormalizeModelId(modelId, providerId)] = config, level, ct))
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
            return;

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

    /// <summary>配置变更处理</summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        var needsRefresh = e.ChangedSections.Length == 0 ||
                           e.ChangedSections.Contains("Providers");

        if (needsRefresh)
            EnqueueRefresh("configuration");
    }

    private void OnProvidersChanged(object? sender, ProvidersChangedEventArgs e)
        => EnqueueRefresh("provider-registry");

    private void EnqueueRefresh(string source)
    {
        long version;
        lock (_cacheLock)
        {
            version = ++_refreshVersion;
        }

        if (!_refreshQueue.Writer.TryWrite(new RefreshRequest(version, source)))
            _logger.LogDebug("模型目录刷新队列已关闭，忽略 {Source} 请求", source);
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
    }

    private async Task RefreshFromProvidersAsync(RefreshRequest request, CancellationToken ct)
    {
        var models = new Dictionary<string, ModelConfig>();

        // 配置驱动：用户级 Providers[*].Models；扩展：仍走 ILlmProvider.GetModelsAsync。
        foreach (var (providerId, providerConfig) in GetUserProviders())
        {
            if (providerConfig.Models is null)
                continue;

            foreach (var (modelId, config) in providerConfig.Models)
                models[ModelRef.Format(providerId, modelId)] = CloneModelConfig(providerId, modelId, config);
        }

        var extensionLoads = _registry.GetProviders()
            .Where(pair => _registry.GetOwnerExtensionId(pair.Key) is not null)
            .Select(pair => LoadProviderModelsAsync(pair.Key, pair.Value, ct));
        var extensionModels = await Task.WhenAll(extensionLoads).ConfigureAwait(false);

        foreach (var (providerId, configurations) in extensionModels)
        {
            foreach (var config in configurations)
            {
                if (string.IsNullOrWhiteSpace(config.Id))
                    continue;

                config.Provider = providerId;
                models[ModelRef.Format(providerId, config.Id)] = config;
            }
        }

        if (!TryReplaceCache(request.Version, models))
        {
            _logger.LogDebug("丢弃过期模型目录刷新 {Version}（当前 {CurrentVersion}）",
                request.Version, Volatile.Read(ref _refreshVersion));
            return;
        }

        ModelConfigChanged?.Invoke(this, new ModelConfigChangedEventArgs
        {
            ChangeType = ModelConfigChangeType.Updated
        });
    }

    private async Task<KeyValuePair<string, IReadOnlyList<ModelConfig>>> LoadProviderModelsAsync(
        string providerId,
        ILlmProvider provider,
        CancellationToken ct)
    {
        try
        {
            var models = await provider.GetModelsAsync(ct).ConfigureAwait(false);
            return new KeyValuePair<string, IReadOnlyList<ModelConfig>>(providerId, models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            Pricing = config.Pricing
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

        if (!_configManager.SeeingAgent.Providers.ContainsKey(providerId))
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
        Action<Dictionary<string, ModelConfig>> update,
        ConfigLevel level,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(providerId))
            return false;

        if (!_configManager.SeeingAgent.Providers.TryGetValue(providerId, out var existingProvider))
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
        update(models);
        provider.Models = models;
        providers[providerId] = provider;

        await _configManager
            .SaveSectionAsync("Providers", providers, ModelStoreLevel, ct)
            .ConfigureAwait(false);
        return true;
    }

    private IReadOnlyDictionary<string, ProviderConfig> GetUserProviders()
        => _configManager.UserSeeingAgent?.Providers
           ?? _configManager.SeeingAgent.Providers
           ?? (IReadOnlyDictionary<string, ProviderConfig>)new Dictionary<string, ProviderConfig>();

    private static ProviderConfig CloneProviderConfig(ProviderConfig config)
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
        _configManager.ConfigChanged -= OnConfigChanged;
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

    private sealed record RefreshRequest(long Version, string Source);
}
