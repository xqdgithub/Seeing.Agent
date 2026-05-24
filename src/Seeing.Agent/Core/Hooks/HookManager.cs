using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Hooks;

public sealed class HookManager : IHookManager
{
    private readonly ConcurrentDictionary<string, List<object>> _handlers = new();
    private readonly ILogger<HookManager> _logger;

    public HookManager(ILogger<HookManager> logger) => _logger = logger;

    public void Register(IHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空 Handler"); return; }
        AddHandler(handler.Spec.Point, handler);
        _logger.LogDebug("注册 Handler: {Point}, Priority={Priority}", handler.Spec.Point, handler.Priority);
    }

    public void RegisterMulti(IMultiHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空多点 Handler"); return; }
        foreach (var spec in handler.Specs) AddHandler(spec.Point, handler);
        _logger.LogDebug("注册多点 Handler: Points={Count}", handler.Specs.Count);
    }

    public bool Remove(IHookHandler handler)
    {
        if (handler == null) return false;
        return RemoveHandler(handler.Spec.Point, handler);
    }

    public bool Clear(HookSpec spec)
    {
        var removed = _handlers.TryRemove(spec.Point, out _);
        if (removed) _logger.LogDebug("清除 Hook 点: {Point}", spec.Point);
        return removed;
    }

    public int Count(HookSpec spec) => _handlers.TryGetValue(spec.Point, out var list) ? list.Count : 0;

    public async Task<HookResult> TriggerAsync(HookPayload payload)
    {
        return payload.Spec.Policy switch
        {
            HookPolicy.Blocking => await ExecuteBlockingAsync(payload),
            HookPolicy.FireAndForget => ExecuteFireAndForget(payload),
            HookPolicy.Parallel => await ExecuteParallelAsync(payload),
            _ => HookResult.Success
        };
    }

    public Task<HookResult> TriggerBlockingAsync(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default) =>
        TriggerAsync(HookPayload.Blocking(spec, sessionId, input, mutable, cancellationToken));

    public void TriggerFireAndForget(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null) =>
        _ = TriggerAsync(HookPayload.FireAndForget(spec, sessionId, input, result));

    public Task TriggerParallelAsync(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        CancellationToken cancellationToken = default) =>
        TriggerAsync(HookPayload.Parallel(spec, sessionId, input, cancellationToken));

    private void AddHandler(string point, object handler)
    {
        var list = _handlers.GetOrAdd(point, _ => new List<object>());
        lock (list)
        {
            list.Add(handler);
            list.Sort((a, b) => GetPriority(a).CompareTo(GetPriority(b)));
        }
    }

    private bool RemoveHandler(string point, object handler)
    {
        if (!_handlers.TryGetValue(point, out var list)) return false;
        lock (list) return list.Remove(handler);
    }

    private static int GetPriority(object handler) =>
        handler is IHookHandler h ? h.Priority :
        handler is IMultiHookHandler m ? m.Priority : 0;

    private List<object> GetHandlers(string point) =>
        _handlers.TryGetValue(point, out var list) ? list : new List<object>();

    private async Task<HookResult> ExecuteBlockingAsync(HookPayload payload)
    {
        var handlers = GetHandlers(payload.Spec.Point);
        if (handlers.Count == 0) return HookResult.Success;

        foreach (var handler in handlers)
        {
            try
            {
                var result = await ExecuteHandlerAsync(handler, payload);
                if (!result.Continue)
                {
                    _logger.LogDebug("Hook 链中断: {Point}", payload.Spec.Point);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler 失败: {Point}", payload.Spec.Point);
                return HookResult.FromError(ex);
            }
        }
        return HookResult.Success;
    }

    private HookResult ExecuteFireAndForget(HookPayload payload)
    {
        var handlers = GetHandlers(payload.Spec.Point);
        if (handlers.Count == 0) return HookResult.Success;

        _ = Task.Run(async () =>
        {
            foreach (var handler in handlers)
            {
                try { await ExecuteHandlerAsync(handler, payload); }
                catch (Exception ex) { _logger.LogError(ex, "Handler 失败: {Point}", payload.Spec.Point); }
            }
        }, payload.CancellationToken);

        return HookResult.Success;
    }

    private async Task<HookResult> ExecuteParallelAsync(HookPayload payload)
    {
        var handlers = GetHandlers(payload.Spec.Point);
        if (handlers.Count == 0) return HookResult.Success;

        await Task.WhenAll(handlers.Select(h => SafeExecuteAsync(h, payload)));
        return HookResult.Success;
    }

    private static async Task<HookResult> ExecuteHandlerAsync(object handler, HookPayload payload)
    {
        if (handler is IHookHandler h) return await h.ExecuteAsync(payload);
        if (handler is IMultiHookHandler m) return await m.ExecuteAsync(payload);
        return HookResult.Success;
    }

    private async Task SafeExecuteAsync(object handler, HookPayload payload)
    {
        try { await ExecuteHandlerAsync(handler, payload); }
        catch (Exception ex) { _logger.LogError(ex, "Handler 失败"); }
    }
}