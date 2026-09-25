using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Session.Core;
using Seeing.Agent.Llm;

using Seeing.Agent.Abstractions.Configuration;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型域门面：目录委托 <see cref="ModelConfigManager"/>，并负责解析与会话读写。
/// <para>
/// 依赖 <see cref="IAgentStore"/> 而非 <see cref="IAgentRegistry"/>，
/// 避免 DI 环：ModelManager → AgentManager → AgentRuntimeManager → IModelManager。
/// </para>
/// </summary>
public sealed class ModelManager : IModelManager
{
    private readonly IModelConfigManager _catalog;
    private readonly IAgentStore _agentStore;

    /// <summary>
    /// 注入模型目录管理器与 Agent 存储构造模型域门面。
    /// </summary>
    public ModelManager(IModelConfigManager catalog, IAgentStore agentStore)
    {
        _catalog = catalog;
        _agentStore = agentStore;
    }

    /// <summary>
    /// 按请求 > 会话 > Agent 配置 > 目录默认的优先级解析 Native 运行模型引用。
    /// </summary>
    public string? ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName)
    {
        if (!string.IsNullOrEmpty(requestModelRef))
            return requestModelRef;

        if (!string.IsNullOrEmpty(sessionModelRef))
            return sessionModelRef;

        // 同步契约点：IAgentStore.Get 为内存快照读取（不阻塞 I/O），避免 sync-over-async
        var agent = _agentStore.Get(agentName);
        if (agent?.Model is { ModelId: { Length: > 0 } } modelRef)
            return modelRef.ToString();

        return _catalog.GetDefaultModel();
    }

    /// <summary>
    /// 按请求 > 会话的优先级解析 ACP 运行模型引用，均缺省返回 null（交由 ACP 端决定）。
    /// </summary>
    public string? ResolveAcpModel(string? requestModelRef, string? sessionModelRef)
    {
        if (!string.IsNullOrEmpty(requestModelRef))
            return requestModelRef;

        if (!string.IsNullOrEmpty(sessionModelRef))
            return sessionModelRef;

        return null;
    }

    /// <summary>
    /// 读取会话当前选中的模型引用，未选择时返回空串。
    /// </summary>
    public string GetSessionModelRef(SessionData session) =>
        session.SelectedModel ?? string.Empty;

    /// <summary>
    /// 将模型引用规范化后写入会话；若实际发生变更则清空不兼容的思考档位并返回 true。
    /// </summary>
    public bool ApplyModelToSession(SessionData session, string? modelRef)
    {
        var trimmed = modelRef?.Trim() ?? string.Empty;
        var normalized = string.IsNullOrEmpty(trimmed)
            ? string.Empty
            : NormalizeCatalogRef(trimmed);

        if (string.Equals(session.SelectedModel ?? string.Empty, normalized, StringComparison.Ordinal))
            return false;

        session.SelectedModel = normalized;
        ClearIncompatibleThinkingEffort(session, normalized);
        return true;
    }

    /// <summary>
    /// 换模型：旧思考档 ∉ 新 levels（或不支持思考）时清空会话字段，与规格 §5 / WebUI 行为对齐。
    /// </summary>
    private void ClearIncompatibleThinkingEffort(SessionData session, string modelRef)
    {
        if (string.IsNullOrWhiteSpace(session.SelectedThinkingEffort))
            return;

        if (string.IsNullOrEmpty(modelRef))
        {
            session.SelectedThinkingEffort = string.Empty;
            return;
        }

        var thinking = GetModel(modelRef)?.Options?.Thinking;
        if (!ThinkingEffortKeys.IsSupported(thinking)
            || thinking!.Levels!.All(l =>
                !string.Equals(l.Key, session.SelectedThinkingEffort, StringComparison.OrdinalIgnoreCase)))
        {
            session.SelectedThinkingEffort = string.Empty;
        }
    }

    /// <summary>
    /// 会话尚无选中模型时按 Agent 配置播种默认模型；ACP 直通会话与已有选择时跳过。
    /// </summary>
    public bool SeedSessionModel(SessionData session, string agentName)
    {
        if (!string.IsNullOrEmpty(session.SelectedModel))
            return false;

        // 同步契约点：IAgentStore.Get 为内存快照读取（不阻塞 I/O），避免 sync-over-async
        var agent = _agentStore.Get(agentName);
        if (agent?.Runtime == AgentRuntime.AcpPassthrough)
            return false;

        return ApplyModelToSession(session, ResolveNativeModel(null, null, agentName));
    }

    /// <summary>
    /// 获取目录中全部模型配置（透传目录管理器）。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModels() => _catalog.GetModels();

    /// <summary>
    /// 按模型 ID 获取模型配置，未找到返回 null。
    /// </summary>
    public ModelConfig? GetModel(string modelId) => _catalog.GetModel(modelId);

    /// <summary>
    /// 获取默认模型引用。
    /// </summary>
    public string? GetDefaultModel() => _catalog.GetDefaultModel();

    /// <summary>
    /// 获取指定 Provider 下的模型配置字典。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId) =>
        _catalog.GetModelsByProvider(providerId);

    /// <summary>
    /// 解析模型生效的类型列表（缺省时按目录规则补全）。
    /// </summary>
    public IReadOnlyList<Seeing.Agent.Abstractions.Llm.ModelType> GetEffectiveTypes(ModelConfig config) =>
        _catalog.GetEffectiveTypes(config);

    /// <summary>
    /// 按模型类型（可选限定 Provider）过滤模型配置。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
        Seeing.Agent.Abstractions.Llm.ModelType type = Seeing.Agent.Abstractions.Llm.ModelType.Text,
        string? providerId = null) =>
        _catalog.GetModelsByType(type, providerId);

    /// <summary>
    /// 判定指定模型是否可被设为默认模型。
    /// </summary>
    public bool CanSetAsDefaultModel(string modelId) => _catalog.CanSetAsDefaultModel(modelId);

    /// <summary>
    /// 刷新模型目录，可限定单个 Provider。
    /// </summary>
    public Task RefreshCatalogAsync(
        string? providerId = null,
        CancellationToken ct = default) =>
        _catalog.RefreshCatalogAsync(providerId, ct);

    /// <summary>
    /// 新增模型配置并持久化到指定配置级别。
    /// </summary>
    public Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.AddModelAsync(modelId, config, level, ct);

    /// <summary>
    /// 更新模型配置并持久化到指定配置级别。
    /// </summary>
    public Task UpdateModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.UpdateModelAsync(modelId, config, level, ct);

    /// <summary>
    /// 删除模型配置并持久化到指定配置级别。
    /// </summary>
    public Task DeleteModelAsync(
        string modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.DeleteModelAsync(modelId, level, ct);

    /// <summary>
    /// 整体保存指定 Provider 的模型集合并持久化到指定配置级别。
    /// </summary>
    public Task SaveModelsAsync(
        string providerId,
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.SaveModelsAsync(providerId, models, level, ct);

    /// <summary>
    /// 设置默认模型并持久化到指定配置级别。
    /// </summary>
    public Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.SetDefaultModelAsync(modelId, level, ct);

    /// <summary>
    /// 模型配置变更事件（透传目录管理器的同名事件）。
    /// </summary>
    public event EventHandler<ModelConfigChangedEventArgs>? ModelConfigChanged
    {
        add => _catalog.ModelConfigChanged += value;
        remove => _catalog.ModelConfigChanged -= value;
    }

    private string NormalizeCatalogRef(string trimmed)
    {
        var config = _catalog.GetModel(trimmed);
        if (config is null)
            return trimmed;

        var keys = _catalog.GetModels()
            .Where(kv => ReferenceEquals(kv.Value, config))
            .Select(kv => kv.Key)
            .ToList();

        if (keys.Count == 1)
            return keys[0];

        var exact = keys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));
        return exact ?? trimmed;
    }
}
