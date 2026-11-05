using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// Provider 管理器实现 - 负责配置驱动 Provider 的注册和配置持久化。
/// </summary>
public class ProviderManager : IProviderManager, IDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILlmClientFactoryResolver _clientFactories;
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
        ILlmClientFactoryResolver clientFactories,
        IModelConfigManager modelManager,
        IProviderRegistry registry,
        Lazy<IModelCapabilityManager> capabilityManager,
        ILogger<ProviderManager> logger)
    {
        _configManager = configManager;
        _clientFactories = clientFactories ?? throw new ArgumentNullException(nameof(clientFactories));
        _modelManager = modelManager;
        _registry = registry;
        _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        _logger = logger;

        RegisterConfiguredProviders();
        _registry.ProvidersChanged += OnProvidersChanged;

        _logger.LogInformation(
            "ProviderManager 已初始化，{Count} 个配置驱动 Provider 已注册（{ProtocolCount} 个协议工厂）",
            _configuredProviders.Count,
            _clientFactories.Protocols.Count);
    }

    /// <summary>
    /// 按协议解析客户端工厂（协议→工厂唯一映射的唯一出口）。
    /// </summary>
    internal ILlmClientFactory? ResolveFactory(string protocol)
        => _clientFactories.Find(protocol);

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

    /// <summary>获取指定 Provider 在指定能力上的客户端；Provider 未注册或未供出该能力返回 null。</summary>
    public ILlmCapabilityClient? GetClient(string providerId, LlmCapabilityKind capability)
        => _registry.GetProvider(providerId)?.GetClient(capability);

    /// <summary>根据模型 ID 与能力解析对应的客户端</summary>
    /// <remarks>
    /// 解析逻辑：
    /// 1. 从 IModelConfigManager 获取模型配置
    /// 2. 根据 ModelConfig.Provider 取该能力的客户端
    /// <para>本出口只按 Provider 声明供客户端，<b>不校验模型类型</b>；需要「模型 ↔ 能力」五层校验时
    /// 走 <see cref="ILlmRouter"/>（spec §7.4）。</para>
    /// </remarks>
    public ILlmCapabilityClient? GetClientForModel(string modelId, LlmCapabilityKind capability)
    {
        var modelConfig = _modelManager.GetModel(modelId);
        if (modelConfig == null)
        {
            _logger.LogWarning("未找到模型配置: {ModelId}", modelId);
            return null;
        }

        return GetClient(modelConfig.Provider, capability);
    }

    #endregion

    #region 连接测试

    /// <summary>
    /// 测试 Provider 在指定能力上的连接（决议 9：能力由调用方给定，Provider 不推导、不猜）。
    /// <para>缺省 <see cref="LlmCapabilityKind.Chat"/> 只为兼容既有调用点；
    /// UI 的「测试连接」应针对当前查看/编辑的能力显式传参（spec §7.13）。</para>
    /// </summary>
    public async Task<bool> TestConnectionAsync(
        string providerId,
        string modelId,
        LlmCapabilityKind capability = LlmCapabilityKind.Chat,
        CancellationToken ct = default)
    {
        var provider = _registry.GetProvider(providerId);
        if (provider is null)
        {
            _logger.LogWarning("未找到 Provider: {ProviderId}", providerId);
            return false;
        }

        try
        {
            return await provider.TestConnectionAsync(modelId, capability, ct);
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
        // 重入短路：Register/Unregister 会在持有 _sync 的临界区内同步 Raise 本事件
        // （见 RegisterConfiguredProviders / RefreshConfiguredProviders 内的调用）。
        // 同线程重入会基于「移除后 / 注册前」的中间快照重复构造并释放 Provider。
        // 检测到已持有 _sync 时直接返回，由外层临界区在完成自身迁移后保持最终一致。
        if (Monitor.IsEntered(_sync))
            return;

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
                // 只注销「确实是配置驱动」的那个实例：若该 id 已被扩展或内置 Provider 接管，
                // 删配置不应把它们一并抹掉。
                if (_registry.IsConfigured(providerId))
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

                // 该 id 已被扩展或内置 Provider 占用时，配置驱动的重建不能顶掉它。
                var origin = _registry.GetOrigin(providerId);
                if (origin is not null && origin != ProviderOrigin.Configured)
                {
                    _logger.LogWarning(
                        "配置驱动 Provider {ProviderId} 已被 {Origin} 占用，跳过重建",
                        providerId,
                        origin);
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

        var origin = _registry.GetOrigin(providerId);
        if (origin is not null && origin != ProviderOrigin.Configured)
        {
            _configuredProviderConfigs[providerId] = CloneConfig(ownedConfig);
            _logger.LogWarning(
                "Provider {ProviderId} 已由 {Origin}（{OwnerId}）注册，跳过配置驱动 Provider",
                providerId,
                origin,
                _registry.GetOwnerId(providerId));
            return;
        }

        var provider = CreateConfiguredProvider(ownedConfig, clientFactory);
        if (provider is null)
            return;

        _registry.Register(new ProviderRegistration(
            provider,
            ProviderOrigin.Configured,
            OwnerId: null,
            // ConfiguredLlmProvider 由 ProviderManager 构造并持有，生命周期归注册表。
            DisposeByRegistry: true));
        _configuredProviders[providerId] = provider;
        _configuredProviderConfigs[providerId] = CloneConfig(ownedConfig);
        _logger.LogDebug("已注册配置驱动 Provider: {ProviderId} ({Type})", providerId, ownedConfig.Type);
    }

    /// <summary>
    /// 构造配置驱动 Provider。
    /// <para>能力名写错时 <c>LlmCapabilityMap.ParseCapabilities</c> 抛 <see cref="ArgumentException"/>（决议 19：
    /// 禁止静默忽略）。这里把错误限定在单个 Provider 上——记 Error 后跳过它，
    /// 一个拼错的 <c>capabilities</c> 不该让整台宿主起不来。</para>
    /// </summary>
    private ConfiguredLlmProvider? CreateConfiguredProvider(ProviderConfig ownedConfig, ILlmClientFactory clientFactory)
    {
        try
        {
            return new ConfiguredLlmProvider(
                ownedConfig,
                clientFactory,
                _logger,
                saveAsync: (cfg, level, token) => SaveProviderAsync(cfg.Id, cfg, level, token),
                _capabilityManager.Value);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(
                ex,
                "Provider 配置非法，已跳过注册: {ProviderId} ({Type}) — {Message}",
                ownedConfig.Id,
                ownedConfig.Type,
                ex.Message);
            return null;
        }
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
        var registration = _registry.GetRegistration(providerId);
        return new ProviderInfo
        {
            Id = provider.Id,
            Name = provider.Name,
            // 协议与能力投影：诊断「这个 Provider 到底供出什么」的唯一去处（决议 15）
            Protocol = provider.Protocol,
            Capabilities = provider.Capabilities,
            // 来源直接取注册记录，不再从 owner 是否为空推断。
            Origin = registration?.Origin ?? ProviderOrigin.Configured,
            OwnerId = registration?.OwnerId,
            MaxRetries = provider.MaxRetries
        };
    }

    /// <summary>该 id 是否由扩展持有（只读判定，用于「扩展 Provider 不支持保存/删除配置」）。</summary>
    private bool IsExtensionProvider(string providerId)
        => _registry.GetOrigin(providerId) == ProviderOrigin.Extension;

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
           previous.ListModels != current.ListModels ||
           !CapabilitySetsEqual(previous.Capabilities, current.Capabilities) ||
           !DictionaryEqual(previous.Options, current.Options);

    /// <summary>
    /// 能力清单是否等价：大小写与顺序无关（配置键经 <c>LlmCapabilityMap</c> 规范化后比较）。
    /// <para>名字非法时按原文比较——让改动仍然触发重建，新配置在构造期被大声拒绝而不是悄悄沿用旧实例。</para>
    /// </summary>
    private static bool CapabilitySetsEqual(List<string>? previous, List<string>? current)
    {
        var before = NormalizeCapabilities(previous);
        var after = NormalizeCapabilities(current);
        return before.SetEquals(after);

        static HashSet<string> NormalizeCapabilities(List<string>? values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values is null)
                return set;

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                set.Add(LlmCapabilityMap.TryParseConfigKey(value, out var kind)
                    ? LlmCapabilityMap.ToConfigKey(kind)
                    : value.Trim());
            }

            return set;
        }
    }

    private static bool DictionaryEqual<TValue>(
        IReadOnlyDictionary<string, TValue>? first,
        IReadOnlyDictionary<string, TValue>? second)
        => ReferenceEquals(first, second) ||
           (first is not null &&
            second is not null &&
            first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var value) &&
                              EqualityComparer<TValue>.Default.Equals(pair.Value, value)));

    /// <summary>
    /// 配置硬编码字段克隆清单之四（另三处在 <see cref="ConfiguredLlmProvider"/>）。
    /// <para><b>漏字段 = 保存一次抹掉配置</b>（Review Focus #5）：新增字段必须四处同步，
    /// 由 <c>ProviderConfigRoundTripTests</c> 守门。</para>
    /// </summary>
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

    #endregion

    /// <summary>
    /// 释放资源：注销 Provider 注册表的变更监听。
    /// </summary>
    public void Dispose()
    {
        _registry.ProvidersChanged -= OnProvidersChanged;
    }
}
