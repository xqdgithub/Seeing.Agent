using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

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
/// 模型配置管理器接口 - 负责模型配置的查询和持久化。
/// <para>类型位于 Abstractions 程序集；实现由 Core 提供。命名空间保持 <c>Seeing.Agent.Llm</c> 以兼容既有引用。</para>
/// </summary>
public interface IModelConfigManager
{
    #region 查询

    /// <summary>获取所有模型配置</summary>
    /// <remarks>模型目录聚合所有已注册 Provider 的模型。</remarks>
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

    /// <summary>解析有效类型：Types 空/null → [Text]</summary>
    IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config);

    /// <summary>
    /// 按类型过滤。默认 type = Text。
    /// 多标签：EffectiveTypes 包含该类型即命中。
    /// </summary>
    IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
        ModelType type = ModelType.Text,
        string? providerId = null);

    /// <summary>是否可作为默认对话模型（有效类型含 Text）</summary>
    bool CanSetAsDefaultModel(string modelId);

    /// <summary>
    /// 重新聚合模型目录并等待本次刷新生效。
    /// </summary>
    /// <param name="providerId">
    /// 为 null 或空白时刷新全部 Provider；
    /// 指定时仅刷新该 Provider（配置型重读 <c>Providers[*].Models</c>，扩展型重新
    /// <c>GetModelsAsync</c>），其它 Provider 的目录条目保留。
    /// </param>
    /// <param name="ct">取消等待或刷新。</param>
    Task RefreshCatalogAsync(
        string? providerId = null,
        CancellationToken ct = default);

    #endregion

    #region 持久化

    /// <summary>添加模型配置</summary>
    Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>更新模型配置</summary>
    Task UpdateModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>删除模型配置</summary>
    Task DeleteModelAsync(
        string modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>批量保存模型配置</summary>
    Task SaveModelsAsync(
        string providerId,
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    /// <summary>设置默认模型</summary>
    Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default);

    #endregion

    #region 事件

    /// <summary>模型配置变更事件</summary>
    event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged;

    #endregion
}
