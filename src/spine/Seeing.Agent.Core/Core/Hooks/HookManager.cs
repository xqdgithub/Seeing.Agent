using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 管理器：按钩子点注册/移除处理器，并按策略（阻塞/并行/即发即忘）触发。
/// </summary>
public class HookManager : IHookManager
{
    private readonly ConcurrentDictionary<string, List<object>> _handlers = new();
    private readonly ILogger<HookManager> _logger;
    private long _version;

    /// <summary>
    /// 已改名为 <c>llm.*</c> 的历史 Hook 点 → 新点名（spec §7.6）。
    /// <para>插件 dll 是编译产物：重命名常量不会让它编译失败，只会让它的注册静默失效。
    /// 这里在登记处显式告警并跳过登记，把「静默不触发」变成「启动即报」。</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_legacyLlmPoints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat.params"] = "llm.params",
            ["chat.headers"] = "llm.headers",
            ["chat.message"] = "llm.message",
            ["chat.after_complete"] = "llm.after_complete",
            ["chat.on_error"] = "llm.on_error",
        };

    /// <summary>
    /// 构造 Hook 管理器。
    /// </summary>
    public HookManager(ILogger<HookManager> logger) => _logger = logger;

    /// <summary>
    /// 处理器集合版本号，单调递增；注册/移除/清空 Hook 时递增，供 schema 缓存失效判定使用。
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// 注册单点 Hook 处理器，按其 Spec.Point 归组并按优先级排序。
    /// </summary>
    public void Register(IHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空 Handler"); return; }
        // 废弃点：告警后跳过登记（版本号不递增——登记未生效，无缓存需要失效）。
        if (!AddHandler(handler.Spec.Point, handler)) return;

        Interlocked.Increment(ref _version);
        _logger.LogDebug("注册 Handler: {Point}, Priority={Priority}", handler.Spec.Point, handler.Priority);
    }

    /// <summary>
    /// 注册多点 Hook 处理器，将其挂到声明的每个钩子点。
    /// </summary>
    public void RegisterMulti(IMultiHookHandler handler)
    {
        if (handler == null) { _logger.LogWarning("尝试注册空多点 Handler"); return; }

        var registered = false;
        foreach (var spec in handler.Specs) registered |= AddHandler(spec.Point, handler);
        if (registered) Interlocked.Increment(ref _version);
        _logger.LogDebug("注册多点 Handler: Points={Count}", handler.Specs.Count);
    }

    /// <summary>
    /// 从对应钩子点移除指定处理器，返回是否移除成功。
    /// </summary>
    public bool Remove(IHookHandler handler)
    {
        if (handler == null) return false;

        var removed = RemoveHandler(handler.Spec.Point, handler);
        if (removed) Interlocked.Increment(ref _version);
        return removed;
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

        if (removed) Interlocked.Increment(ref _version);
        return removed;
    }

    /// <summary>
    /// 清除指定钩子点下的全部处理器，返回是否有被清除的钩子点。
    /// </summary>
    public bool Clear(HookSpec spec)
    {
        var removed = _handlers.TryRemove(spec.Point, out _);
        if (!removed) return false;

        Interlocked.Increment(ref _version);
        _logger.LogDebug("清除 Hook 点: {Point}", spec.Point);
        return true;
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

    /// <summary>
    /// 把处理器挂到指定钩子点；<paramref name="point"/> 是已改名的废弃 <c>chat.*</c> 点时
    /// 打告警并跳过登记，返回 <c>false</c>。
    /// </summary>
    private bool AddHandler(string point, object handler)
    {
        if (s_legacyLlmPoints.TryGetValue(point, out var renamed))
        {
            _logger.LogWarning(
                "Hook 点 {Point} 已改名为 llm.*（现为 {Renamed}），本次注册不会触发，请改订阅新点",
                point, renamed);
            return false;
        }

        var list = _handlers.GetOrAdd(point, _ => new List<object>());
        lock (list)
        {
            // 去重：同一处理器重复注册会让 Hook 执行多次（如 TokenBudget 重复接线导致预算双计）。
            // 允许同一实例挂在**不同**钩子点（多点处理器），只拦同一点上的重复登记。
            if (list.Contains(handler))
                return false;

            list.Add(handler);
            list.Sort((a, b) => GetPriority(a).CompareTo(GetPriority(b)));
        }

        return true;
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