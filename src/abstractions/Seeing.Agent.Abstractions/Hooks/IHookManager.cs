namespace Seeing.Agent.Abstractions.Hooks;

public interface IHookManager
{
    /// <summary>
    /// 处理器集合版本号，单调递增；注册/移除/清空 Hook 时递增，供 schema 缓存失效判定使用。
    /// </summary>
    /// <remarks>
    /// 契约要求实现必须返回真实版本号。不得以默认实现返回常量，否则 schema 缓存将永不失效。
    /// </remarks>
    long Version { get; }

    void Register(IHookHandler handler);
    void RegisterMulti(IMultiHookHandler handler);
    bool Remove(IHookHandler handler);
    bool Remove(IMultiHookHandler handler);
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