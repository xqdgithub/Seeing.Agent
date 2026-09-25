using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// Provider 管理器实现 - 负责配置驱动 Provider 的注册和配置持久化。
/// </summary>
public class ProviderManager : IProviderManager, IDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILlmClientFactory[] _clientFactories;
    private readonly IModelConfigManager _modelManager;
    private readonly IProviderRegistry _registry;
    // Lazy：打断 ModelCapabilityManager → ReloadOrchestrator → ProviderReloadHandler → ProviderManager → MCM 环
    private readonly Lazy<IModelCapabilityManager> _capabilityManager;
    private readonly ILogger<ProviderManager> _logger;
    // 串行化配置驱动 Provider 的内部字典写路径（刷新 / 注册表变更事件 / 保存 / 删除），
    // 读路径经不可变快照或防御性快照读取。
    private readonly object _sync = new();
    private readonly Dictionary<string, ConfiguredLlmProvider> _configuredProviders = [];
    private readonly Dictionary<string, ProviderConfig> _configuredProviderConfigs = [];

    /// <summary>当前配置驱动的 Provider 字典（用户级 providers.json）。</summary>
    private Dictionary<string, ProviderConfig> ConfiguredProviders
        => _configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");

    /// <summary>
    /// 注入配置管理器与客户端工厂等依赖构造 Provider 管理器，并注册配置驱动 Provider。
    /// </summary>
    public ProviderManager(
        UnifiedConfigManager configManager,
        IEnumerable<ILlmClientFactory> clientFactories,
        IModelConfigManager modelManager,
        IProviderRegistry registry,
        Lazy<IModelCapabilityManager> capabilityManager,
        ILogger<ProviderManager> logger)
    {
        _configManager = configManager;
        _clientFactories = clientFactories?.ToArray() ?? [];
        _modelManager = modelManager;
        _registry = registry;
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        _logger = logger;

        RegisterConfiguredProviders();
        _registry.ProvidersChanged += OnProvidersChanged;

        _logger.LogInformation(
            "ProviderManager 已初始化，{Count} 个配置驱动 Provider 已注册（{FactoryCount} 个工厂）",
            _configuredProviders.Count,
            _clientFactories.Length);
    }

    /// <summary>
    /// 解析支持指定类型的首个工厂（同类型多工厂时 first wins）。
    /// </summary>
    internal ILlmClientFactory? ResolveFactory(string type)
        => _clientFactories.FirstOrDefault(factory => factory.SupportsType(type));

    #region 查询

    /// <summary>获取所有已注册 Provider 的信息</summary>
    public IReadOnlyDictionary<string, ProviderInfo> GetProviders()
        => _registry.GetProviders().ToDictionary(
            pair => pair.Key,
            pair => CreateProviderInfo(pair.Key, pair.Value));

    /// <summary>获取指定 Provider 的信息</summary>
    public ProviderInfo? GetProvider(string providerId)
    {
        var provider = _registry.GetProvider(providerId);
        return provider is null ? null : CreateProviderInfo(providerId, provider);
    }

    /// <inheritdoc />
    public bool TryGetConfigurable(string providerId, out IConfigurableLlmProvider? configurable)
    {
        configurable = null;
        var provider = _registry.GetProvider(providerId);
        if (provider is IConfigurableLlmProvider c)
        {
            configurable = c;
            return true;
        }

        return false;
    }

    #endregion

    #region 客户端管理

    /// <summary>获取指定 Provider 的客户端</summary>
    public ILlmClient? GetClient(string providerId)
        => _registry.GetProvider(providerId)?.GetClient();

    /// <summary>根据模型 ID 解析对应的客户端</summary>
    public ILlmClient? GetClientForModel(string modelId)
    {
        var modelConfig = _modelManager.GetModel(modelId);
        if (modelConfig == null)
        {
            _logger.LogWarning("未找到模型配置: {ModelId}", modelId);
            return null;
        }

        return GetClient(modelConfig.Provider);
    }

    #endregion

    #region 连接测试

    /// <summary>测试 Provider 连接</summary>
    public async Task<bool> TestConnectionAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        var provider = _registry.GetProvider(providerId);
        if (provider is null)
        {
            _logger.LogWarning("未找到 Provider: {ProviderId}", providerId);
            return false;
        }

        try
        {
            return await provider.TestConnectionAsync(modelId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试 Provider 连接失败: {ProviderId}", providerId);
            return false;
        }
    }

    #endregion

    #region 持久化

    /// <summary>保存 Provider 配置（仅用户级）</summary>
    public async Task SaveProviderAsync(
        string providerId,
        ProviderConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        if (IsExtensionProvider(providerId))
        {
            _logger.LogWarning("扩展 Provider 不支持保存配置: {ProviderId}", providerId);
            return;
        }

        if (level != ConfigLevel.User)
        {
            _logger.LogDebug(
                "Providers 为 UserOnly，忽略请求级 {RequestedLevel}（Provider={ProviderId}）",
                level,
                providerId);
        }

        var providersAtLevel = await LoadProvidersAtLevelAsync(ConfigLevel.User, ct).ConfigureAwait(false);
        var saved = CloneConfig(config);
        saved.Id = providerId;
        saved.Type = ProviderTypes.Normalize(saved.Type);
        // 连接保存时若未带 Models，保留用户级已有模型，避免冲掉目录
        if (saved.Models is null || saved.Models.Count == 0)
        {
            if (providersAtLevel.TryGetValue(providerId, out var atLevel) &&
                atLevel.Models is { Count: > 0 })
            {
                saved.Models = new Dictionary<string, ModelConfig>(atLevel.Models);
            }
        }

        providersAtLevel[providerId] = saved;

        await _configManager
            .SaveSectionAsync("Providers", providersAtLevel, ConfigLevel.User, new[] { providerId }, ct)
            .ConfigureAwait(false);

        // SaveSectionAsync 已同步缓存，无需 ReloadAsync 触发全量重载事件；
        // 自订阅已迁移为 ReloadHandler，保存后需显式刷新配置驱动 Provider
        RefreshConfiguredProviders();

        _logger.LogInformation("已保存 Provider 配置: {ProviderId} (User)", providerId);
    }

    /// <summary>删除 Provider 配置（仅用户级）</summary>
    public async Task DeleteProviderAsync(
        string providerId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default)
    {
        if (IsExtensionProvider(providerId))
        {
            _logger.LogWarning("扩展 Provider 不支持删除配置: {ProviderId}", providerId);
            return;
        }

        if (level != ConfigLevel.User)
        {
            _logger.LogDebug(
                "Providers 为 UserOnly，忽略请求级 {RequestedLevel}（Provider={ProviderId}）",
                level,
                providerId);
        }

        var providersAtLevel = await LoadProvidersAtLevelAsync(ConfigLevel.User, ct).ConfigureAwait(false);
        if (!providersAtLevel.Remove(providerId))
        {
            _logger.LogWarning(
                "用户级不存在 Provider 配置，跳过删除: {ProviderId}",
                providerId);
            return;
        }

        await _configManager
            .SaveSectionAsync("Providers", providersAtLevel, ConfigLevel.User, new[] { providerId }, ct)
            .ConfigureAwait(false);

        // SaveSectionAsync 已同步缓存，无需 ReloadAsync 触发全量重载事件；
        // 自订阅已迁移为 ReloadHandler，删除后需显式刷新配置驱动 Provider
        RefreshConfiguredProviders();

        _logger.LogInformation("已删除 Provider 配置: {ProviderId} (User)", providerId);
    }

    #endregion

    #region 私有方法

    private void OnProvidersChanged(object? sender, ProvidersChangedEventArgs e)
    {
        lock (_sync)
        {
            foreach (var providerId in _configuredProviderConfigs.Keys.ToArray())
            {
                if (e.Providers.ContainsKey(providerId))
                    continue;

                if (!ConfiguredProviders.TryGetValue(providerId, out var currentConfig))
                {
                    _configuredProviders.Remove(providerId);
                    _configuredProviderConfigs.Remove(providerId);
                    continue;
                }

                RegisterConfiguredProvider(providerId, currentConfig);
            }
        }
    }

    private void RegisterConfiguredProviders()
    {
        lock (_sync)
        {
            foreach (var (providerId, providerConfig) in ConfiguredProviders)
                RegisterConfiguredProvider(providerId, providerConfig);
        }
    }

    internal void RefreshConfiguredProviders()
    {
        lock (_sync)
        {
            var currentProviders = ConfiguredProviders;
            foreach (var providerId in _configuredProviders.Keys.Except(currentProviders.Keys).ToArray())
            {
                if (_registry.GetProvider(providerId) is not null &&
                    _registry.GetOwnerExtensionId(providerId) is null)
                    _registry.Unregister(providerId);

                _configuredProviders.Remove(providerId);
                _configuredProviderConfigs.Remove(providerId);
                _logger.LogDebug("已移除配置驱动 Provider: {ProviderId}", providerId);
            }

            foreach (var (providerId, config) in currentProviders)
            {
                if (!_configuredProviders.ContainsKey(providerId))
                {
                    RegisterConfiguredProvider(providerId, config);
                    continue;
                }

                if (!RequiresRebuild(_configuredProviderConfigs[providerId], config))
                    continue;

                var ownerExtensionId = _registry.GetOwnerExtensionId(providerId);
                if (ownerExtensionId is not null)
                {
                    _logger.LogWarning(
                        "配置驱动 Provider {ProviderId} 已被扩展 {ExtensionId} 覆盖，跳过重建",
                        providerId,
                        ownerExtensionId);
                    continue;
                }

                if (_registry.GetProvider(providerId) is not null)
                    _registry.Unregister(providerId);

                _configuredProviders.Remove(providerId);
                _configuredProviderConfigs.Remove(providerId);
                RegisterConfiguredProvider(providerId, config);
            }
        }
    }

    private void RegisterConfiguredProvider(string providerId, ProviderConfig config)
    {
        var ownedConfig = CloneConfig(config);
        ownedConfig.Id = providerId;
        ownedConfig.Type = ProviderTypes.Normalize(ownedConfig.Type);

        var clientFactory = ResolveFactory(ownedConfig.Type);
        if (clientFactory is null)
        {
            _logger.LogWarning("不支持的 Provider 类型: {ProviderId} ({Type})", providerId, ownedConfig.Type);
            return;
        }

        var ownerExtensionId = _registry.GetOwnerExtensionId(providerId);
        if (ownerExtensionId is not null)
        {
            _configuredProviderConfigs[providerId] = CloneConfig(ownedConfig);
            _logger.LogWarning(
                "Provider {ProviderId} 已由扩展 {ExtensionId} 注册，跳过配置驱动 Provider",
                providerId,
                ownerExtensionId);
            return;
        }

        var provider = new ConfiguredLlmProvider(
            ownedConfig,
            clientFactory,
            _logger,
            saveAsync: (cfg, level, token) => SaveProviderAsync(cfg.Id, cfg, level, token),
            _capabilityManager.Value);
        _registry.Register(provider, ownerExtensionId: null);
        _configuredProviders[providerId] = provider;
        _configuredProviderConfigs[providerId] = CloneConfig(ownedConfig);
        _logger.LogDebug("已注册配置驱动 Provider: {ProviderId} ({Type})", providerId, ownedConfig.Type);
    }

    private async Task<Dictionary<string, ProviderConfig>> LoadProvidersAtLevelAsync(
        ConfigLevel level,
        CancellationToken ct)
    {
        var providers = await _configManager
            .GetSectionAtLevelAsync<Dictionary<string, ProviderConfig>>("Providers", level, ct)
            .ConfigureAwait(false);
        return providers is { Count: > 0 }
            ? providers.ToDictionary(
                pair => pair.Key,
                pair => CloneConfig(pair.Value),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
    }

    private ProviderInfo CreateProviderInfo(string providerId, ILlmProvider provider)
    {
        var ownerExtensionId = _registry.GetOwnerExtensionId(providerId);
        return new ProviderInfo
        {
            Id = provider.Id,
            Name = provider.Name,
            Source = ownerExtensionId is null ? ProviderSource.Configured : ProviderSource.Extension,
            OwnerExtensionId = ownerExtensionId,
            MaxRetries = provider.MaxRetries
        };
    }

    private bool IsExtensionProvider(string providerId)
        => _registry.GetOwnerExtensionId(providerId) is not null;

    /// <summary>
    /// 判断 Provider 实例是否需要重建。模型目录变化由 <see cref="ModelConfigManager"/> 按配置直接刷新，
    /// 不触发实例重建（避免无谓的注册表变更与全量目录刷新）。
    /// </summary>
    internal static bool RequiresRebuild(ProviderConfig previous, ProviderConfig current)
        => previous.Type != current.Type ||
           !string.Equals(previous.ApiKey, current.ApiKey, StringComparison.Ordinal) ||
           !string.Equals(previous.BaseUrl, current.BaseUrl, StringComparison.Ordinal) ||
           !string.Equals(previous.Proxy, current.Proxy, StringComparison.Ordinal) ||
           previous.UseProxy != current.UseProxy ||
           previous.Timeout != current.Timeout ||
           !DictionaryEqual(previous.Headers, current.Headers) ||
           !string.Equals(previous.Name, current.Name, StringComparison.Ordinal) ||
           previous.MaxRetries != current.MaxRetries ||
           previous.RetryBaseDelayMs != current.RetryBaseDelayMs ||
           previous.RetryMaxDelayMs != current.RetryMaxDelayMs ||
           previous.RetryTotalBudgetMs != current.RetryTotalBudgetMs ||
           !string.Equals(previous.DefaultModel, current.DefaultModel, StringComparison.Ordinal) ||
           !DictionaryEqual(previous.Options, current.Options);

    private static bool DictionaryEqual<TValue>(
        IReadOnlyDictionary<string, TValue>? first,
        IReadOnlyDictionary<string, TValue>? second)
        => ReferenceEquals(first, second) ||
           (first is not null &&
            second is not null &&
            first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var value) &&
                              EqualityComparer<TValue>.Default.Equals(pair.Value, value)));

    private static ProviderConfig CloneConfig(ProviderConfig config)
        => new()
        {
            Id = config.Id,
            Type = ProviderTypes.Normalize(config.Type),
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

    #endregion

    /// <summary>
    /// 释放资源：注销 Provider 注册表的变更监听。
    /// </summary>
    public void Dispose()
    {
        _registry.ProvidersChanged -= OnProvidersChanged;
    }
}
