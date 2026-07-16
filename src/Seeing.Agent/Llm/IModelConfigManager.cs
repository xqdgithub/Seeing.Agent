using Seeing.Agent.Configuration;

namespace Seeing.Agent.Llm;

/// <summary>
/// 模型配置变更事件参数
/// </summary>
public class ModelConfigChangedEventArgs : EventArgs
{
    public string ModelId { get; init; } = "";
    public ModelConfigChangeType ChangeType { get; init; }
    public ModelConfig? OldConfig { get; init; }
    public ModelConfig? NewConfig { get; init; }
}

/// <summary>
/// 模型配置变更类型
/// </summary>
public enum ModelConfigChangeType
{
    Added,
    Updated,
    Deleted
}

/// <summary>
/// 模型配置管理器接口 - 负责模型配置的查询和持久化
/// </summary>
public interface IModelConfigManager
{
    #region 查询

    /// <summary>获取所有模型配置</summary>
    /// <remarks>
    /// 模型来源优先级：
    /// 1. Provider.Models (最高优先级，用于覆盖)
    /// 2. Models (全局模型目录)
    /// 3. ModelScope.Models (ModelScope 风格配置)
    /// </remarks>
    IReadOnlyDictionary<string, ModelConfig> GetModels();

    /// <summary>获取指定模型配置</summary>
    /// <param name="modelId">模型 ID，支持以下格式：
    /// - "gpt-4o" (不带前缀)
    /// - "openai/gpt-4o" (带 Provider 前缀)
    /// </param>
    ModelConfig? GetModel(string modelId);

    /// <summary>获取默认模型 ID</summary>
    string? GetDefaultModel();

    /// <summary>获取指定 Provider 下的模型列表</summary>
    IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId);

    #endregion

    #region 持久化

    /// <summary>添加模型配置</summary>
    Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>更新模型配置</summary>
    Task UpdateModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>删除模型配置</summary>
    Task DeleteModelAsync(
        string modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>批量保存模型配置</summary>
    Task SaveModelsAsync(
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    /// <summary>设置默认模型</summary>
    Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default);

    #endregion

    #region 事件

    /// <summary>模型配置变更事件</summary>
    event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged;

    #endregion
}
