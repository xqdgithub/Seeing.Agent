using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Llm;

/// <summary>
/// Provider 管理器实现 - 负责配置驱动 Provider 的注册和配置持久化。
/// </summary>
public class ProviderManager : IProviderManager, IDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILlmClientFactory _clientFactory;
    private readonly IModelConfigManager _modelManager;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<ProviderManager> _logger;
    private readonly Dictionary<string, ConfiguredLlmProvider> _configuredProviders = [];
    private readonly Dictionary<string, ProviderConfig> _configuredProviderConfigs = [];

    public ProviderManager(
        UnifiedConfigManager configManager,
        ILlmClientFactory clientFactory,
        IModelConfigManager modelManager,
        IProviderRegistry registry,
        ILogger<ProviderManager> logger)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _modelManager = modelManager;
        _registry = registry;
        _logger = logger;

        // 监听配置变更
        _configManager.ConfigChanged += OnConfigChanged;

        RegisterConfiguredProviders();
        _registry.ProvidersChanged += OnProvidersChanged;

        _logger.LogInformation(
            "ProviderManager 已初始化，{Count} 个配置驱动 Provider 已注册",
            _configuredProviders.Count);
    }

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

    /// <summary>获取默认 Provider ID</summary>
    public string? GetDefaultProvider()
        => _configManager.SeeingAgent.DefaultProvider;

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

        await _configManager.SaveSectionAsync("Providers", providersAtLevel, ConfigLevel.User, ct)
            .ConfigureAwait(false);
        await _configManager.ReloadAsync(ct).ConfigureAwait(false);

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

        await _configManager.SaveSectionAsync("Providers", providersAtLevel, ConfigLevel.User, ct)
            .ConfigureAwait(false);
        await _configManager.ReloadAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("已删除 Provider 配置: {ProviderId} (User)", providerId);
    }

    /// <summary>设置默认 Provider</summary>
    public async Task SetDefaultProviderAsync(
        string? providerId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        if (providerId is not null && IsExtensionProvider(providerId))
        {
            _logger.LogWarning("扩展 Provider 不支持设为默认 Provider: {ProviderId}", providerId);
            return;
        }

        await _configManager.SaveSectionAsync("DefaultProvider", providerId ?? (string)null!, level, ct);

        _logger.LogInformation("已设置默认 Provider: {ProviderId}", providerId ?? "(空)");
    }

    #endregion

    #region 私有方法

    /// <summary>配置变更处理</summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        var needsRefresh = e.ChangedSections.Length == 0 ||
                           e.ChangedSections.Contains("Providers");

        if (needsRefresh)
        {
            _logger.LogDebug("配置变更，重建配置驱动 Provider: {Sections}", string.Join(", ", e.ChangedSections));
            RefreshConfiguredProviders();
        }
    }

    private void OnProvidersChanged(object? sender, ProvidersChangedEventArgs e)
    {
        foreach (var providerId in _configuredProviderConfigs.Keys.ToArray())
        {
            if (e.Providers.ContainsKey(providerId))
                continue;

            if (!_configManager.SeeingAgent.Providers.TryGetValue(providerId, out var currentConfig))
            {
                _configuredProviders.Remove(providerId);
                _configuredProviderConfigs.Remove(providerId);
                continue;
            }

            RegisterConfiguredProvider(providerId, currentConfig);
        }
    }

    private void RegisterConfiguredProviders()
    {
        foreach (var (providerId, providerConfig) in _configManager.SeeingAgent.Providers)
            RegisterConfiguredProvider(providerId, providerConfig);
    }

    private void RefreshConfiguredProviders()
    {
        var currentProviders = _configManager.SeeingAgent.Providers;
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

    private void RegisterConfiguredProvider(string providerId, ProviderConfig config)
    {
        var ownedConfig = CloneConfig(config);
        ownedConfig.Id = providerId;

        if (!_clientFactory.SupportsType(ownedConfig.Type))
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
            _clientFactory,
            _logger,
            saveAsync: (cfg, level, token) => SaveProviderAsync(cfg.Id, cfg, level, token));
        _registry.Register(provider, ownerExtensionId: null);
        _configuredProviders[providerId] = provider;
        _configuredProviderConfigs[providerId] = CloneConfig(ownedConfig);
        _logger.LogDebug("已注册配置驱动 Provider: {ProviderId} ({Type})", providerId, ownedConfig.Type);
    }

    private async Task<Dictionary<string, ProviderConfig>> LoadProvidersAtLevelAsync(
        ConfigLevel level,
        CancellationToken ct)
    {
        var optionsAtLevel = await _configManager
            .GetSeeingAgentOptionsAtLevelAsync(level, ct)
            .ConfigureAwait(false);
        return optionsAtLevel?.Providers is { Count: > 0 } providers
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

    private static bool RequiresRebuild(ProviderConfig previous, ProviderConfig current)
        => previous.Type != current.Type ||
           !string.Equals(previous.ApiKey, current.ApiKey, StringComparison.Ordinal) ||
           !string.Equals(previous.BaseUrl, current.BaseUrl, StringComparison.Ordinal) ||
           previous.Timeout != current.Timeout ||
           !DictionaryEqual(previous.Headers, current.Headers) ||
           !string.Equals(previous.Name, current.Name, StringComparison.Ordinal) ||
           previous.MaxRetries != current.MaxRetries ||
           !string.Equals(previous.DefaultModel, current.DefaultModel, StringComparison.Ordinal) ||
           !DictionaryEqual(previous.Models, current.Models) ||
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

    #endregion

    public void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
        _registry.ProvidersChanged -= OnProvidersChanged;
    }
}
