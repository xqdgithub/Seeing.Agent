namespace Seeing.Agent.Llm;

/// <summary>
/// LLM Provider 注册表。
/// </summary>
public interface IProviderRegistry
{
    IReadOnlyDictionary<string, ILlmProvider> GetProviders();

    ILlmProvider? GetProvider(string id);

    string? GetOwnerExtensionId(string id);

    void Register(ILlmProvider provider, string? ownerExtensionId = null);

    bool Unregister(string id);

    int UnregisterByOwner(string ownerExtensionId);

    event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;
}
