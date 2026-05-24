namespace Seeing.Agent.Core.Hooks;

public interface IHookManager
{
    void Register(IHookHandler handler);
    void RegisterMulti(IMultiHookHandler handler);
    bool Remove(IHookHandler handler);
    bool Clear(HookSpec spec);
    int Count(HookSpec spec);

    Task<HookResult> TriggerAsync(HookPayload payload);

    Task<HookResult> TriggerBlockingAsync(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default);

    void TriggerFireAndForget(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null);

    Task TriggerParallelAsync(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        CancellationToken cancellationToken = default);
}