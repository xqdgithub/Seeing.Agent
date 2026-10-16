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

    /// <summary>获取所有「已启用」模型配置（按 <c>ModelSelection</c> 白名单过滤后的子集）。</summary>
    /// <remarks>模型目录聚合所有已注册 Provider 的模型；未启用（白名单外）的模型不出现在此视图。</remarks>
    IReadOnlyDictionary<string, ModelConfig> GetModels();

    /// <summary>获取全部模型配置（不经启用清单过滤），供管理 UI 勾选。</summary>
    IReadOnlyDictionary<string, ModelConfig> GetCatalogModels();

    /// <summary>获取指定模型配置（含未启用模型；隐藏模型仍可解析、仍可执行）。</summary>
    /// <param name="modelId">模型 ID，支持以下格式：
    /// - "gpt-4o" (不带前缀)
    /// - "openai/gpt-4o" (带 Provider 前缀)
    /// </param>
    ModelConfig? GetModel(string modelId);

    /// <summary>获取默认模型 ID</summary>
    string? GetDefaultModel();

    /// <summary>获取指定 Provider 下「已启用」的模型列表。</summary>
    IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId);

    /// <summary>获取指定 Provider 下的全部模型列表（不经启用清单过滤）。</summary>
    IReadOnlyDictionary<string, ModelConfig> GetCatalogModelsByProvider(string providerId);

    /// <summary>获取指定 Provider 的启用模型 ID 列表（可能为空）。</summary>
    IReadOnlyList<string> GetEnabledModelIds(string providerId);

    /// <summary>解析有效类型：Types 空/null → [Text]</summary>
    IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config);

    /// <summary>
    /// 按类型过滤「已启用」模型。默认 type = Text。
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

    /// <summary>
    /// 设置指定 Provider 的启用模型清单（allowlist）。
    /// <para>写入 <c>ModelSelection</c> 配置节并重建启用视图；不重拉 Provider。</para>
    /// </summary>
    /// <param name="providerId">目标 Provider ID。</param>
    /// <param name="modelIds">启用的模型 ID 列表；为空表示该 Provider 全部隐藏。</param>
    /// <param name="ct">取消。</param>
    Task SetEnabledModelsAsync(
        string providerId,
        IReadOnlyList<string> modelIds,
        CancellationToken ct = default);

    /// <summary>
    /// 获取指定模型的用户覆盖补丁（无覆盖返回 null）。
    /// <para>供管理 UI 显示「已覆盖」标记与编辑回填。</para>
    /// </summary>
    ModelOverride? GetModelOverride(string providerId, string modelId);

    /// <summary>
    /// 应用模型元数据覆盖：将目标模型调整为 <paramref name="desired"/> 指定的最终值，
    /// 仅持久化与「无覆盖基线」存在差异的字段（避免固化默认值、远端更新后不再跟随）。
    /// <para>写用户级 <c>ModelOverrides</c> 节并重建视图；支持补录目录中不存在的模型。</para>
    /// </summary>
    /// <param name="providerId">目标 Provider ID。</param>
    /// <param name="modelId">模型 ID（不含 Provider 前缀）。</param>
    /// <param name="desired">期望的最终元数据（null 字段视为不关心）。</param>
    /// <param name="ct">取消。</param>
    Task UpsertModelOverrideAsync(
        string providerId,
        string modelId,
        ModelOverride desired,
        CancellationToken ct = default);

    /// <summary>
    /// 移除指定模型的用户覆盖，恢复默认值。
    /// <para>补录模型移除覆盖后将从目录中消失。</para>
    /// </summary>
    Task RemoveModelOverrideAsync(
        string providerId,
        string modelId,
        CancellationToken ct = default);

    #endregion

    #region 事件

    /// <summary>模型配置变更事件</summary>
    event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged;

    #endregion
}
