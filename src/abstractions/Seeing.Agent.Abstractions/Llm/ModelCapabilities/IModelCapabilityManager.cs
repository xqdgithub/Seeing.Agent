namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型能力编排入口（唯一应用 API）。
/// </summary>
public interface IModelCapabilityManager
{
    ValueTask<ModelConfig> TryEnrichIfEnabledAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ModelCapabilitySourceRegistration> GetRegistrations();

    IModelCapabilitySource? GetSource(string sourceId);

    event EventHandler<ModelCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    /// <summary>
    /// 统一变更出口：抬升 CapabilitiesChanged，并 Publish ModelCapabilitiesChange。
    /// <paramref name="invalidateModelCatalog"/> 写入信号字段（单次覆盖）；
    /// null 时取自 Options.InvalidateModelCatalogOnChange。
    /// </summary>
    ValueTask NotifyChangedAsync(
        ModelCapabilitiesChangeReason reason,
        IReadOnlyList<string>? affectedSourceIds = null,
        ModelCapabilitySourceChangeKind? sourceKind = null,
        bool? invalidateModelCatalog = null,
        CancellationToken cancellationToken = default);
}
