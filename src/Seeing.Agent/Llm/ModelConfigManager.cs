using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// 模型配置管理器实现 - 负责模型配置的查询、索引和持久化
/// </summary>
public class ModelConfigManager : IModelConfigManager, IDisposable
{
    private readonly UnifiedConfigManager _configManager;
    private readonly ILogger<ModelConfigManager> _logger;

    // 模型索引缓存
    private IReadOnlyDictionary<string, ModelConfig> _modelCache = new Dictionary<string, ModelConfig>();
    private Dictionary<string, Dictionary<string, ModelConfig>>? _providerIndex;

    /// <summary>模型配置变更事件</summary>
    public event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged;

    public ModelConfigManager(
        UnifiedConfigManager configManager,
        ILogger<ModelConfigManager> logger)
    {
        _configManager = configManager;
        _logger = logger;

        // 监听配置变更
        _configManager.ConfigChanged += OnConfigChanged;

        // 初始化索引
        RefreshCache();

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

        // 1. 直接匹配
        if (_modelCache.TryGetValue(modelId, out var config))
            return config;

        // 2. 带 Provider 前缀匹配 (如 openai/gpt-4o)
        foreach (var providerId in _configManager.SeeingAgent.Providers.Keys)
        {
            if (_modelCache.TryGetValue($"{providerId}/{modelId}", out config))
                return config;
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

        _providerIndex ??= BuildProviderIndex();
        return _providerIndex.TryGetValue(providerId, out var models)
            ? models
            : new Dictionary<string, ModelConfig>();
    }

    #endregion

    #region 持久化

    /// <summary>添加模型配置</summary>
    public async Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        var models = new Dictionary<string, ModelConfig>(_configManager.SeeingAgent.Models ?? new())
        {
            [modelId] = config
        };

        await _configManager.SaveSectionAsync("Models", models, level, ct);

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
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        var oldConfig = GetModel(modelId);

        var models = new Dictionary<string, ModelConfig>(_configManager.SeeingAgent.Models ?? new())
        {
            [modelId] = config
        };

        await _configManager.SaveSectionAsync("Models", models, level, ct);

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
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        var oldConfig = GetModel(modelId);

        var models = new Dictionary<string, ModelConfig>(_configManager.SeeingAgent.Models ?? new());
        models.Remove(modelId);

        await _configManager.SaveSectionAsync("Models", models, level, ct);

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
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        await _configManager.SaveSectionAsync("Models", models, level, ct);

        _logger.LogInformation("已保存 {Count} 个模型配置", models.Count);
    }

    /// <summary>设置默认模型</summary>
    public async Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default)
    {
        await _configManager.SaveSectionAsync("DefaultModel", modelId, level, ct);

        _logger.LogInformation("已设置默认模型: {ModelId}", modelId ?? "(空)");
    }

    #endregion

    #region 私有方法

    /// <summary>配置变更处理</summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // 判断是否需要刷新模型缓存
        var needsRefresh = e.ChangedSections.Length == 0 ||
                           e.ChangedSections.Contains("Models") ||
                           e.ChangedSections.Contains("ModelScope") ||
                           e.ChangedSections.Contains("Providers") ||
                           e.ChangedSections.Contains("DefaultProvider");

        if (needsRefresh)
        {
            _logger.LogDebug("配置变更，刷新模型缓存: {Sections}", string.Join(", ", e.ChangedSections));
            RefreshCache();
        }
    }

    /// <summary>刷新模型缓存</summary>
    private void RefreshCache()
    {
        var models = new Dictionary<string, ModelConfig>();
        var options = _configManager.SeeingAgent;

        // 1. 加载 Models（全局模型目录）
        if (options.Models != null)
        {
            foreach (var (id, config) in options.Models)
            {
                EnsureModelDefaults(id, config, options.DefaultProvider);
                models[id] = config;
            }
        }

        // 2. 加载 ModelScope.Models
        if (options.ModelScope?.Models != null)
        {
            foreach (var (id, config) in options.ModelScope.Models)
            {
                EnsureModelDefaults(id, config, options.DefaultProvider);
                // 不覆盖已存在的模型
                if (!models.ContainsKey(id))
                    models[id] = config;
            }
        }

        // 3. 加载 Provider.Models（最高优先级）
        foreach (var (providerId, providerConfig) in options.Providers)
        {
            if (providerConfig.Models == null) continue;

            foreach (var (id, config) in providerConfig.Models)
            {
                EnsureModelDefaults(id, config, providerId);

                // 只使用 provider/modelId 格式存储，避免重复
                var fullKey = $"{providerId}/{id}";
                models[fullKey] = config;
            }
        }

        _modelCache = models;
        _providerIndex = null; // 懒加载重建

        _logger.LogDebug("模型缓存已刷新，共 {Count} 个模型", _modelCache.Count);
    }

    /// <summary>确保模型配置的默认值</summary>
    private static void EnsureModelDefaults(string modelId, ModelConfig config, string? defaultProvider)
    {
        if (string.IsNullOrEmpty(config.Id))
            config.Id = modelId;

        if (string.IsNullOrEmpty(config.Provider) && !string.IsNullOrEmpty(defaultProvider))
            config.Provider = defaultProvider;
    }

    /// <summary>构建 Provider 索引</summary>
    private Dictionary<string, Dictionary<string, ModelConfig>> BuildProviderIndex()
    {
        var index = new Dictionary<string, Dictionary<string, ModelConfig>>();

        foreach (var (key, config) in _modelCache)
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
        _configManager.ConfigChanged -= OnConfigChanged;
    }
}
