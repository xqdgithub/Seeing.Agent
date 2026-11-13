using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Hooks.Directory.Tests;

/// <summary>
/// 只记录登记与摘除。不执行处理器；需要真实链式触发时用 <see cref="Seeing.Agent.Core.Hooks.HookManager"/>。
/// </summary>
public sealed class RecordingHookManager : IHookManager
{
    public List<IHookHandler> Registered { get; } = [];
    public List<IMultiHookHandler> RegisteredMulti { get; } = [];
    public List<IHookHandler> Removed { get; } = [];
    public List<IMultiHookHandler> RemovedMulti { get; } = [];
    public int ClearCount { get; private set; }
    public long Version { get; private set; }

    public void Register(IHookHandler handler)
    {
        if (handler is null)
            return;

        Registered.Add(handler);
        Version++;
    }

    public void RegisterMulti(IMultiHookHandler handler)
    {
        if (handler is null)
            return;

        RegisteredMulti.Add(handler);
        Version++;
    }

    public bool Remove(IHookHandler handler)
    {
        if (handler is null || !Registered.Remove(handler))
            return false;

        Removed.Add(handler);
        Version++;
        return true;
    }

    public bool Remove(IMultiHookHandler handler)
    {
        if (handler is null || !RegisteredMulti.Remove(handler))
            return false;

        RemovedMulti.Add(handler);
        Version++;
        return true;
    }

    public bool Clear(HookSpec spec)
    {
        ClearCount++;
        var removed = Registered.RemoveAll(handler => handler.Spec.Point == spec.Point);
        removed += RegisteredMulti.RemoveAll(handler => handler.Specs.Any(item => item.Point == spec.Point));
        if (removed > 0)
            Version++;
        return removed > 0;
    }

    public int Count(HookSpec spec) =>
        Registered.Count(handler => handler.Spec.Point == spec.Point)
        + RegisteredMulti.Count(handler => handler.Specs.Any(item => item.Point == spec.Point));

    public Task<HookResult> TriggerAsync(HookPayload payload) =>
        Task.FromResult(HookResult.Success);

    public Task<HookResult> TriggerBlockingAsync(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HookResult.Success);

    public void TriggerFireAndForget(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null)
    {
    }

    public Task TriggerParallelAsync(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
