using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// Core 内 Null 实现：enrich 原样返回；无 registrations；Notify 为 no-op。
/// </summary>
public sealed class NullModelCapabilityManager : IModelCapabilityManager
{
    /// <summary>
    /// 共享单例实例。
    /// </summary>
    public static NullModelCapabilityManager Instance { get; } = new();

    /// <summary>
    /// 能力变更事件（Null 实现：空访问器，永不触发）。
    /// </summary>
    public event EventHandler<ModelCapabilitiesChangedEventArgs>? CapabilitiesChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// 原样返回模型配置，不做任何能力增强。
    /// </summary>
    public ValueTask<ModelConfig> TryEnrichIfEnabledAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(model);
    }

    /// <summary>
    /// 返回空的能力来源注册列表。
    /// </summary>
    public IReadOnlyList<ModelCapabilitySourceRegistration> GetRegistrations()
        => [];

    /// <summary>
    /// 恒返回 null（无任何已登记的能力来源）。
    /// </summary>
    public IModelCapabilitySource? GetSource(string sourceId) => null;

    /// <summary>
    /// 空操作：仅响应取消请求，不发布任何变更通知。
    /// </summary>
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
