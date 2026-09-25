using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 管理器：按钩子点注册/移除处理器，并按策略（阻塞/并行/即发即忘）触发。
/// </summary>
public class HookManager : IHookManager
{
    private readonly ConcurrentDictionary<string, List<object>> _handlers = new();
    private readonly ILogger<HookManager> _logger;

    /// <summary>
    /// 构造 Hook 管理器。
    /// </summary>
    public HookManager(ILogger<HookManager> logger) => _logger = logger;

    /// <summary>
    /// 注册单点 Hook 处理器，按其 Spec.Point 归组并按优先级排序。
    /// </summary>
    public void Register(IHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空 Handler"); return; }
        AddHandler(handler.Spec.Point, handler);
        _logger.LogDebug("注册 Handler: {Point}, Priority={Priority}", handler.Spec.Point, handler.Priority);
    }

    /// <summary>
    /// 注册多点 Hook 处理器，将其挂到声明的每个钩子点。
    /// </summary>
    public void RegisterMulti(IMultiHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空多点 Handler"); return; }
        foreach (var spec in handler.Specs) AddHandler(spec.Point, handler);
        _logger.LogDebug("注册多点 Handler: Points={Count}", handler.Specs.Count);
    }

    /// <summary>
    /// 从对应钩子点移除指定处理器，返回是否移除成功。
    /// </summary>
    public bool Remove(IHookHandler handler)
    {
        if (handler == null) return false;
        return RemoveHandler(handler.Spec.Point, handler);
    }

    /// <summary>
    /// 从处理器声明的全部钩子点移除该多点处理器，返回是否至少移除一处。
    /// </summary>
    public bool Remove(IMultiHookHandler handler)
    {
        if (handler == null) return false;

        var removed = false;
        foreach (var spec in handler.Specs)
            removed |= RemoveHandler(spec.Point, handler);

        return removed;
    }

    /// <summary>
    /// 清除指定钩子点下的全部处理器，返回是否有被清除的钩子点。
    /// </summary>
    public bool Clear(HookSpec spec)
    {
        var removed = _handlers.TryRemove(spec.Point, out _);
        if (removed) _logger.LogDebug("清除 Hook 点: {Point}", spec.Point);
        return removed;
    }

    /// <summary>
    /// 统计指定钩子点当前注册的处理器数量。
    /// </summary>
    public int Count(HookSpec spec) => _handlers.TryGetValue(spec.Point, out var list) ? list.Count : 0;

    /// <summary>
    /// 按载荷声明的策略触发钩子点，聚合返回 Hook 结果。
    /// </summary>
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

    /// <summary>
    /// 以阻塞策略触发钩子点：等待全部处理器完成，可被拦截终止。
    /// </summary>
    public Task<HookResult> TriggerBlockingAsync(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default) =>
        TriggerAsync(HookPayload.Blocking(spec, sessionId, input, mutable, cancellationToken));

    /// <summary>
    /// 以即发即忘策略触发钩子点：后台执行处理器，不等待结果。
    /// </summary>
    public void TriggerFireAndForget(
        HookSpec spec, string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null) =>
        _ = TriggerAsync(HookPayload.FireAndForget(spec, sessionId, input, result));

    /// <summary>
    /// 以并行策略触发钩子点：并发执行全部处理器并等待完成。
    /// </summary>
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

    private List<object> GetHandlers(string point)
    {
        if (!_handlers.TryGetValue(point, out var list))
            return new List<object>();

        lock (list)
        {
            return list.ToList();
        }
    }

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