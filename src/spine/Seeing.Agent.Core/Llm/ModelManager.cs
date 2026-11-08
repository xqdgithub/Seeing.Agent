using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
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
    /// 按请求 > 会话 > Agent 配置 > 能力默认值（<c>GetDefaultModel(Chat)</c>）的优先级
    /// 解析 Native 运行模型引用。优先级顺序不变（spec §7.14.3）。
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

        return _catalog.GetDefaultModel(LlmCapabilityKind.Chat);
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
    /// <para>会话<b>首次</b>绑定模型时会把模型的「默认档」播种进会话：默认档是初值，
    /// 只在会话还没有自己的选择时生效一次，之后会话里的值才是唯一真相。
    /// 换到另一个模型不重新播种——否则用户在会话里选的「不指定」会被悄悄顶掉。</para>
    /// </summary>
    public bool ApplyModelToSession(SessionData session, string? modelRef)
    {
        var trimmed = modelRef?.Trim() ?? string.Empty;
        var normalized = string.IsNullOrEmpty(trimmed)
            ? string.Empty
            : NormalizeCatalogRef(trimmed);

        if (string.Equals(session.SelectedModel ?? string.Empty, normalized, StringComparison.Ordinal))
            return false;

        // 「首次绑定」= 之前根本没有模型；这是默认档唯一一次的介入时机。
        var isFirstBind = string.IsNullOrEmpty(session.SelectedModel);

        session.SelectedModel = normalized;
        ClearIncompatibleThinkingEffort(session, normalized);
        if (isFirstBind)
            SeedThinkingEffortFromModelDefault(session, normalized);
        return true;
    }

    /// <summary>
    /// 会话尚未指定思考档时，用模型声明的默认档播种（一次性初值）。
    /// <para>播种后会话字段即成为唯一真相：后续请求不再回落到模型默认档，
    /// 因此「不指定」与「未选过」的区别落在<b>绑定时刻</b>，不需要额外的三态字段。</para>
    /// </summary>
    private void SeedThinkingEffortFromModelDefault(SessionData session, string modelRef)
    {
        if (!string.IsNullOrWhiteSpace(session.SelectedThinkingEffort) || string.IsNullOrEmpty(modelRef))
            return;

        var thinking = GetModel(modelRef)?.Options?.Thinking;
        if (!ThinkingEffortKeys.IsSupported(thinking) || string.IsNullOrWhiteSpace(thinking!.Default))
            return;

        // 默认档必须真的在档位表里，否则播进去也是个永远命中不了的值。
        var defaultKey = thinking.Default!.Trim();
        if (!thinking.Levels!.Any(l => string.Equals(l.Key, defaultKey, StringComparison.OrdinalIgnoreCase)))
            return;

        session.SelectedThinkingEffort = defaultKey;
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
    /// 获取目录中「已启用」的模型配置（透传目录管理器）。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModels() => _catalog.GetModels();

    /// <summary>
    /// 获取目录中全部模型配置（不经启用清单过滤，透传目录管理器）。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetCatalogModels() => _catalog.GetCatalogModels();

    /// <summary>
    /// 按模型 ID 获取模型配置（含未启用模型），未找到返回 null。
    /// </summary>
    public ModelConfig? GetModel(string modelId) => _catalog.GetModel(modelId);

    /// <summary>
    /// 获取指定能力的全局默认模型引用（严格查表，未配置返回 null）。
    /// </summary>
    public string? GetDefaultModel(LlmCapabilityKind capability) => _catalog.GetDefaultModel(capability);

    /// <summary>
    /// 获取全部已配置的能力默认值（透传目录管理器）。
    /// </summary>
    public IReadOnlyDictionary<LlmCapabilityKind, string> GetAllDefaultModels() =>
        _catalog.GetAllDefaultModels();

    /// <summary>
    /// 获取指定 Provider 下「已启用」的模型配置字典。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId) =>
        _catalog.GetModelsByProvider(providerId);

    /// <summary>
    /// 获取指定 Provider 下全部模型配置字典（不经启用清单过滤）。
    /// </summary>
    public IReadOnlyDictionary<string, ModelConfig> GetCatalogModelsByProvider(string providerId) =>
        _catalog.GetCatalogModelsByProvider(providerId);

    /// <summary>
    /// 获取指定 Provider 的启用模型 ID 列表。
    /// </summary>
    public IReadOnlyList<string> GetEnabledModelIds(string providerId) =>
        _catalog.GetEnabledModelIds(providerId);

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
    /// 判定指定模型能否被设为该能力的默认模型（不再硬编码 Text 类型）。
    /// </summary>
    public bool CanSetAsDefaultModel(string modelId, LlmCapabilityKind capability) =>
        _catalog.CanSetAsDefaultModel(modelId, capability);

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
    /// 设置某能力的默认模型并持久化到指定配置级别（写时校验前缀 / 存在性 / 能力）。
    /// </summary>
    public Task SetDefaultModelAsync(
        LlmCapabilityKind capability,
        string? modelId,
        ConfigLevel level = ConfigLevel.User,
        CancellationToken ct = default) =>
        _catalog.SetDefaultModelAsync(capability, modelId, level, ct);

    /// <summary>
    /// 查默认值并经 Router 解析客户端（透传目录管理器）。
    /// </summary>
    public bool TryResolveDefault<TClient>(
        LlmCapabilityKind capability,
        out TClient? client,
        out string? reason)
        where TClient : class, ILlmCapabilityClient =>
        _catalog.TryResolveDefault(capability, out client, out reason);

    /// <summary>
    /// 校验全部能力默认值（只报告，不自动清除）。
    /// </summary>
    public IReadOnlyDictionary<LlmCapabilityKind, string> ValidateDefaultModels() =>
        _catalog.ValidateDefaultModels();

    /// <summary>
    /// 设置指定 Provider 的启用模型清单并持久化。
    /// </summary>
    public Task SetEnabledModelsAsync(
        string providerId,
        IReadOnlyList<string> modelIds,
        CancellationToken ct = default) =>
        _catalog.SetEnabledModelsAsync(providerId, modelIds, ct);

    /// <summary>
    /// 获取指定模型的用户元数据覆盖补丁（无覆盖返回 null）。
    /// </summary>
    public ModelOverride? GetModelOverride(string providerId, string modelId) =>
        _catalog.GetModelOverride(providerId, modelId);

    /// <summary>
    /// 应用模型元数据覆盖（仅持久化与基线有差异的字段，支持补录）。
    /// </summary>
    public Task UpsertModelOverrideAsync(
        string providerId,
        string modelId,
        ModelOverride desired,
        CancellationToken ct = default) =>
        _catalog.UpsertModelOverrideAsync(providerId, modelId, desired, ct);

    /// <summary>
    /// 移除指定模型的用户元数据覆盖，恢复默认值。
    /// </summary>
    public Task RemoveModelOverrideAsync(
        string providerId,
        string modelId,
        CancellationToken ct = default) =>
        _catalog.RemoveModelOverrideAsync(providerId, modelId, ct);

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

        var keys = _catalog.GetCatalogModels()
            .Where(kv => ReferenceEquals(kv.Value, config))
            .Select(kv => kv.Key)
            .ToList();

        if (keys.Count == 1)
            return keys[0];

        var exact = keys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));
        return exact ?? trimmed;
    }
}
