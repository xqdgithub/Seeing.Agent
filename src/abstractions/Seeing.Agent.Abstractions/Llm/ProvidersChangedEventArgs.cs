namespace Seeing.Agent.Abstractions.Llm;

public sealed class ProvidersChangedEventArgs : EventArgs
{
    public ProvidersChangedEventArgs(
        IReadOnlyDictionary<string, ILlmProvider> providers,
        IReadOnlyList<string>? changedProviderIds = null,
        IReadOnlyList<string>? removedProviderIds = null)
    {
        Providers = providers;
        ChangedProviderIds = changedProviderIds ?? Array.Empty<string>();
        RemovedProviderIds = removedProviderIds ?? Array.Empty<string>();
    }

    public IReadOnlyDictionary<string, ILlmProvider> Providers { get; }

    /// <summary>
    /// 本次新增或替换（重建）的 Provider id。
    /// <para>空表示未提供粒度信息，消费方应回退全量处理。</para>
    /// </summary>
    public IReadOnlyList<string> ChangedProviderIds { get; }

    /// <summary>本次移除的 Provider id。</summary>
    public IReadOnlyList<string> RemovedProviderIds { get; }
}
