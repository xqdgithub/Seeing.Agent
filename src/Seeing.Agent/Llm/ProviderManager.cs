using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Collections.Concurrent;

namespace Seeing.Agent.Llm;

/// <summary>
/// Provider 管理器实现 - 负责 Provider 配置和客户端生命周期
/// </summary>
public class ProviderManager : IProviderManager, IDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILlmClientFactory _clientFactory;
    private readonly IModelConfigManager _modelManager;
    private readonly ILogger<ProviderManager> _logger;

    // 客户端缓存
    private readonly ConcurrentDictionary<string, ILlmClient> _clients = new();

    public ProviderManager(
        UnifiedConfigManager configManager,
        ILlmClientFactory clientFactory,
        IModelConfigManager modelManager,
        ILogger<ProviderManager> logger)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _modelManager = modelManager;
        _logger = logger;

        // 监听配置变更
        _configManager.ConfigChanged += OnConfigChanged;

        // 初始化客户端
        InitializeClients();

        _logger.LogInformation("ProviderManager 已初始化，{Count} 个客户端就绪", _clients.Count);
    }

    #region 查询

    /// <summary>获取所有 Provider 配置</summary>
    public IReadOnlyDictionary<string, ProviderConfig> GetProviders()
        => _configManager.SeeingAgent.Providers;

    /// <summary>获取指定 Provider 配置</summary>
    public ProviderConfig? GetProvider(string providerId)
        => _configManager.SeeingAgent.Providers.TryGetValue(providerId, out var config)
            ? config
            : null;

    /// <summary>获取默认 Provider ID</summary>
    public string? GetDefaultProvider()
        => _configManager.SeeingAgent.DefaultProvider;

    #endregion

    #region 客户端管理

    /// <summary>获取指定 Provider 的客户端</summary>
    public ILlmClient? GetClient(string providerId)
        => _clients.TryGetValue(providerId, out var client) ? client : null;

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
    public async Task<bool> TestConnectionAsync(string providerId, CancellationToken ct = default)
    {
        var client = GetClient(providerId);
        if (client == null)
        {
            _logger.LogWarning("未找到 Provider 客户端: {ProviderId}", providerId);
            return false;
        }

        try
        {
            return await client.TestConnectionAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "测试 Provider 连接失败: {ProviderId}", providerId);
            return false;
        }
    }

    #endregion

    #region 持久化

    /// <summary>保存 Provider 配置</summary>
    public async Task SaveProviderAsync(
        string providerId,
        ProviderConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        var providers = new Dictionary<string, ProviderConfig>(_configManager.SeeingAgent.Providers)
        {
            [providerId] = config
        };

        await _configManager.SaveSectionAsync("Providers", providers, level, ct);

        _logger.LogInformation("已保存 Provider 配置: {ProviderId}", providerId);
    }

    /// <summary>删除 Provider 配置</summary>
    public async Task DeleteProviderAsync(
        string providerId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        var providers = new Dictionary<string, ProviderConfig>(_configManager.SeeingAgent.Providers);
        providers.Remove(providerId);

        await _configManager.SaveSectionAsync("Providers", providers, level, ct);

        _logger.LogInformation("已删除 Provider 配置: {ProviderId}", providerId);
    }

    /// <summary>设置默认 Provider</summary>
    public async Task SetDefaultProviderAsync(
        string? providerId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        await _configManager.SaveSectionAsync("DefaultProvider", providerId, level, ct);

        _logger.LogInformation("已设置默认 Provider: {ProviderId}", providerId ?? "(空)");
    }

    #endregion

    #region 私有方法

    /// <summary>配置变更处理</summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // 判断是否需要刷新客户端缓存
        var needsRefresh = e.ChangedSections.Length == 0 ||
                           e.ChangedSections.Contains("Providers");

        if (needsRefresh)
        {
            _logger.LogDebug("配置变更，刷新客户端缓存: {Sections}", string.Join(", ", e.ChangedSections));
            RefreshClients();
        }
    }

    /// <summary>初始化客户端</summary>
    private void InitializeClients()
    {
        foreach (var (providerId, providerConfig) in GetProviders())
        {
            TryCreateClient(providerId, providerConfig);
        }
    }

    /// <summary>刷新客户端缓存</summary>
    private void RefreshClients()
    {
        var currentProviders = GetProviders();
        var currentProviderIds = currentProviders.Keys.ToHashSet();
        var cachedProviderIds = _clients.Keys.ToHashSet();

        // 移除不再存在的客户端
        foreach (var removed in cachedProviderIds.Except(currentProviderIds))
        {
            if (_clients.TryRemove(removed, out _))
            {
                _logger.LogDebug("已移除客户端: {ProviderId}", removed);
            }
        }

        // 添加新的客户端或更新现有客户端
        foreach (var (providerId, config) in currentProviders)
        {
            // 如果配置类型变更，需要重建客户端
            if (_clients.TryGetValue(providerId, out var existingClient))
            {
                if (existingClient.ProviderType != config.Type)
                {
                    _clients.TryRemove(providerId, out _);
                    TryCreateClient(providerId, config);
                }
            }
            else
            {
                TryCreateClient(providerId, config);
            }
        }
    }

    /// <summary>尝试创建客户端</summary>
    private void TryCreateClient(string providerId, ProviderConfig config)
    {
        // 确保 ID 一致
        if (string.IsNullOrWhiteSpace(config.Id))
            config.Id = providerId;

        if (!_clientFactory.SupportsType(config.Type))
        {
            _logger.LogWarning("不支持的 Provider 类型: {ProviderId} ({Type})", providerId, config.Type);
            return;
        }

        try
        {
            var client = _clientFactory.Create(config);
            _clients[providerId] = client;
            _logger.LogDebug("已创建客户端: {ProviderId} ({Type})", providerId, config.Type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建客户端失败: {ProviderId}", providerId);
        }
    }

    #endregion

    public void Dispose()
    {
        _configManager.ConfigChanged -= OnConfigChanged;
    }
}
