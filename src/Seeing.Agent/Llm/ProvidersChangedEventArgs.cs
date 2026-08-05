namespace Seeing.Agent.Llm;

public sealed class ProvidersChangedEventArgs : EventArgs
{
    public ProvidersChangedEventArgs(IReadOnlyDictionary<string, ILlmProvider> providers)
    {
        Providers = providers;
    }

    public IReadOnlyDictionary<string, ILlmProvider> Providers { get; }
}
