using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// Core 内 Null 实现：enrich 原样返回；无 registrations；Notify 为 no-op。
/// </summary>
public sealed class NullModelCapabilityManager : IModelCapabilityManager
{
    public static NullModelCapabilityManager Instance { get; } = new();

    public event EventHandler<ModelCapabilitiesChangedEventArgs>? CapabilitiesChanged
    {
        add { }
        remove { }
    }

    public ValueTask<ModelConfig> TryEnrichIfEnabledAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(model);
    }

    public IReadOnlyList<ModelCapabilitySourceRegistration> GetRegistrations()
        => [];

    public IModelCapabilitySource? GetSource(string sourceId) => null;

    public ValueTask NotifyChangedAsync(
        ModelCapabilitiesChangeReason reason,
        IReadOnlyList<string>? affectedSourceIds = null,
        ModelCapabilitySourceChangeKind? sourceKind = null,
        bool? invalidateModelCatalog = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
