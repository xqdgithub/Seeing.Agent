using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Llm;

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

    public ModelManager(IModelConfigManager catalog, IAgentStore agentStore)
    {
        _catalog = catalog;
        _agentStore = agentStore;
    }

    public string? ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName)
    {
        if (!string.IsNullOrEmpty(requestModelRef))
            return requestModelRef;

        if (!string.IsNullOrEmpty(sessionModelRef))
            return sessionModelRef;

        var agent = _agentStore.GetAsync(agentName).GetAwaiter().GetResult();
        if (agent?.Model is { ModelId: { Length: > 0 } } modelRef)
            return modelRef.ToString();

        return _catalog.GetDefaultModel();
    }

    public string? ResolveAcpModel(string? requestModelRef, string? sessionModelRef)
    {
        if (!string.IsNullOrEmpty(requestModelRef))
            return requestModelRef;

        if (!string.IsNullOrEmpty(sessionModelRef))
            return sessionModelRef;

        return null;
    }

    public string GetSessionModelRef(SessionData session) =>
        session.SelectedModel ?? string.Empty;

    public bool ApplyModelToSession(SessionData session, string? modelRef)
    {
        var trimmed = modelRef?.Trim() ?? string.Empty;
        var normalized = string.IsNullOrEmpty(trimmed)
            ? string.Empty
            : NormalizeCatalogRef(trimmed);

        if (string.Equals(session.SelectedModel ?? string.Empty, normalized, StringComparison.Ordinal))
            return false;

        session.SelectedModel = normalized;
        return true;
    }

    public bool SeedSessionModel(SessionData session, string agentName)
    {
        if (!string.IsNullOrEmpty(session.SelectedModel))
            return false;

        var agent = _agentStore.GetAsync(agentName).GetAwaiter().GetResult();
        if (agent?.Runtime == AgentRuntime.AcpPassthrough)
            return false;

        return ApplyModelToSession(session, ResolveNativeModel(null, null, agentName));
    }

    public IReadOnlyDictionary<string, ModelConfig> GetModels() => _catalog.GetModels();

    public ModelConfig? GetModel(string modelId) => _catalog.GetModel(modelId);

    public string? GetDefaultModel() => _catalog.GetDefaultModel();

    public IReadOnlyDictionary<string, ModelConfig> GetModelsByProvider(string providerId) =>
        _catalog.GetModelsByProvider(providerId);

    public IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config) =>
        _catalog.GetEffectiveTypes(config);

    public IReadOnlyDictionary<string, ModelConfig> GetModelsByType(
        ModelType type = ModelType.Text,
        string? providerId = null) =>
        _catalog.GetModelsByType(type, providerId);

    public bool CanSetAsDefaultModel(string modelId) => _catalog.CanSetAsDefaultModel(modelId);

    public Task AddModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.AddModelAsync(modelId, config, level, ct);

    public Task UpdateModelAsync(
        string modelId,
        ModelConfig config,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.UpdateModelAsync(modelId, config, level, ct);

    public Task DeleteModelAsync(
        string modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.DeleteModelAsync(modelId, level, ct);

    public Task SaveModelsAsync(
        Dictionary<string, ModelConfig> models,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.SaveModelsAsync(models, level, ct);

    public Task SetDefaultModelAsync(
        string? modelId,
        ConfigLevel level = ConfigLevel.Project,
        CancellationToken ct = default) =>
        _catalog.SetDefaultModelAsync(modelId, level, ct);

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
