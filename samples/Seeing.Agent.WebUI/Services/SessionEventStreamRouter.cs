using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话事件流路由器（Singleton）。
/// 按 sessionId 对 ExecutionEventPublisher 的流只订阅一次（每会话单 Loop），
/// 再将事件广播给该会话的全部消费者；支持引用快照去重（skipSet 按 Loop 创建时构建）
/// 与 replay 补历史。消费者经 IServiceScopeFactory 创建为 Scoped，Router 维护
/// circuit → (scope, consumer) 映射，circuit 关闭时统一释放。
/// <para>
/// <b>circuit 隔离（C1）</b>：consumer 实例按 circuit 隔离——同会话不同 circuit
/// （多标签页）各自持有独立实例与 scope，一方 circuit 关闭不影响另一方订阅。
/// 幂等复用键均含 circuitId。
/// </para>
/// <para>
/// <b>实例化维度</b>：EventStreamHandler 为「会话维度」consumer，键 (circuit, session, type)，
/// 同 circuit 同会话复用；TaskCardAggregator 为「circuit 维度」consumer，键 (circuit, type)，
/// 同 circuit 跨会话复用（页面经 Rebind 切换父会话），避免访问新会话累积实例与 scope（I2）。
/// </para>
/// <para>
/// <b>loop 重启（I1）</b>：会话空闲清理（CompleteSession）后消费 loop 自然结束；
/// 消费者已挂载但 loop 已停止时，再次 AttachConsumer 视为需要重启订阅（重建 skipSet + cts + loop）。
/// </para>
/// <para>
/// 每 (circuit, session) 可注册多个不同消费者类型：首个会话维度 GetOrCreateConsumer 登记的
/// 类型为该会话「主 consumer」（如渲染 handler），其余为辅助 consumer。主 consumer 摘除
/// （页面会话切换/关闭）时释放其 scope；辅助 consumer 摘除子会话或 Rebind 切换父会话时
/// 仅移除订阅、不释放实例，供后续复用。
/// </para>
/// <para>
/// EventStreamHandler 需运行时 sessionId 构造，GetOrCreateConsumer 对其走「按会话工厂」
/// 直接 new（绕过 DI 占位注册的空 sessionId 实例），不挂 scope。
/// </para>
/// <para>
/// <b>释放前 flush（I3）</b>：ReleaseConsumer 释放 scope 前，若 consumer 实现 IDisposable
/// 先调用其 Dispose（TaskCardAggregator 借此落盘防抖窗口内未持久化的 TaskSteps）。
/// </para>
/// </summary>
public sealed class SessionEventStreamRouter : IDisposable
{
    private readonly IChatOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionEventStreamRouter> _logger;

    private readonly ConcurrentDictionary<string, SessionSubscription> _subscriptions = new();

    /// <summary>经 DI 解析的 Scoped 消费者 → 其 scope（circuit 关闭 / 主 consumer 摘除时释放）</summary>
    private readonly ConcurrentDictionary<IStreamConsumer, IServiceScope> _consumerScopes = new();

    /// <summary>消费者 → circuit 归属（DetachAllForCircuit 据此统一释放）</summary>
    private readonly ConcurrentDictionary<IStreamConsumer, string> _consumerCircuit = new();

    /// <summary>幂等复用键（会话维度，C1 含 circuit）：(circuit, session, type) → consumer（同 circuit 同会话同类型复用实例）</summary>
    private readonly ConcurrentDictionary<(string circuit, string session, Type type), IStreamConsumer> _consumersByKey = new();

    /// <summary>幂等复用键（circuit 维度，I2）：(circuit, type) → consumer（同 circuit 跨会话复用，如 TaskCardAggregator）</summary>
    private readonly ConcurrentDictionary<(string circuit, Type type), IStreamConsumer> _circuitConsumersByKey = new();

    /// <summary>每 (circuit, session) 主 consumer（首个会话维度 consumer 登记；摘除主 consumer 时释放其 scope）</summary>
    private readonly ConcurrentDictionary<(string circuit, string session), IStreamConsumer> _mainConsumerBySession = new();

    private bool _disposed;

    public SessionEventStreamRouter(
        IChatOrchestrator orchestrator,
        IServiceScopeFactory scopeFactory,
        ILogger<SessionEventStreamRouter> logger)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 注册消费者。已注册且消费 loop 存活时幂等跳过；replay=true 时先补发当前缓冲历史。
    /// 首个消费者触发该会话的订阅 Loop（引用快照去重）。
    /// <para>
    /// I1：消费者已挂载但消费 loop 已停止（会话空闲清理 CompleteSession 后 loop 自然结束、
    /// <c>Loop=null</c> 但消费者仍挂载）时，视为需要重启订阅——重建 skipSet + cts + consume loop，
    /// 使同页新提交（再次 AttachConsumer）可恢复事件消费。
    /// </para>
    /// </summary>
    public void AttachConsumer(string sessionId, IStreamConsumer consumer, bool replay = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sub = _subscriptions.GetOrAdd(sessionId, _ => new SessionSubscription());
        var alreadyMounted = !sub.Consumers.TryAdd(consumer, 0);

        if (alreadyMounted)
        {
            var loop = sub.Loop;
            if (loop != null && !loop.IsCompleted)
                return; // 已挂载且 loop 存活：幂等跳过
        }

        var buffered = _orchestrator.GetBufferedEvents(sessionId) ?? Array.Empty<IMessageEvent>();

        if (replay)
        {
            foreach (var evt in buffered)
                SafeInvoke(consumer, evt);
        }

        lock (sub)
        {
            // 双检：进入锁后 loop 可能已被其他 Attach 重建
            var currentLoop = sub.Loop;
            if (currentLoop != null && !currentLoop.IsCompleted)
                return;

            // 重启订阅：递增 LoopGeneration，使旧 loop（若其 finally 尚未收尾）不再管理订阅状态
            sub.LoopGeneration++;
            var generation = sub.LoopGeneration;
            var skipSet = new HashSet<IMessageEvent>(buffered, ReferenceEqualityComparer.Instance);
            var oldCts = sub.Cts;
            var cts = new CancellationTokenSource();
            sub.Cts = cts;
            sub.Loop = ConsumeLoopAsync(sessionId, sub, cts.Token, skipSet, generation);
            oldCts?.Dispose();
        }
    }

    /// <summary>
    /// 按 circuit 创建 Scoped 消费者并登记映射。幂等：
    /// <list type="bullet">
    /// <item>EventStreamHandler（会话维度）：同 (circuit, session, type) 已存在时直接复用（不新建 scope）。</item>
    /// <item>TaskCardAggregator（circuit 维度，I2）：同 (circuit, type) 已存在时跨会话复用同一实例（页面经 Rebind 切换），
    /// 避免每访问新父会话累积聚合器实例与 scope。</item>
    /// </list>
    /// 调用方随后需自行 AttachConsumer。首个会话维度 consumer 为该 (circuit, session)「主 consumer」
    /// （DetachConsumer 摘除时释放其 scope）；EventStreamHandler 走按会话工厂直接构造（sessionId 运行时确定），不挂 scope。
    /// </summary>
    public T GetOrCreateConsumer<T>(string sessionId, string circuitId) where T : IStreamConsumer
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // I2：TaskCardAggregator 为 circuit 维度消费者，同 circuit 跨会话复用
        if (typeof(T) == typeof(TaskCardAggregator))
            return (T)(object)GetOrCreateCircuitScopedConsumer(circuitId);

        var key = (circuitId, sessionId, typeof(T));
        if (_consumersByKey.TryGetValue(key, out var existing))
            return (T)existing;

        var consumer = CreateConsumer<T>(sessionId);
        _consumerCircuit[consumer] = circuitId;

        // 主 consumer：每 (circuit, session) 首个会话维度登记。辅助 consumer 摘除子会话 /
        // Rebind 切换父会话时 Detach 旧父不得释放其实例，因此仅主 consumer 摘除才释放 scope。
        _mainConsumerBySession.TryAdd((circuitId, sessionId), consumer);

        // 竞争窗口：另一线程刚登记同 (circuit, session, type) → 释放新建资源，复用既有实例
        if (!_consumersByKey.TryAdd(key, consumer))
        {
            ReleaseConsumer(consumer);
            return (T)_consumersByKey[key];
        }
        return consumer;
    }

    /// <summary>
    /// 获取/创建 circuit 维度的 Scoped 消费者（同 circuit 跨会话复用，I2 方案 A）。
    /// 不登记为任何 (circuit, session) 的主 consumer——仅在 circuit 关闭时统一释放。
    /// </summary>
    private TaskCardAggregator GetOrCreateCircuitScopedConsumer(string circuitId)
    {
        var key = (circuitId, typeof(TaskCardAggregator));
        if (_circuitConsumersByKey.TryGetValue(key, out var existing))
            return (TaskCardAggregator)existing;

        var consumer = CreateConsumer<TaskCardAggregator>(string.Empty);
        _consumerCircuit[consumer] = circuitId;

        // 竞争窗口：另一线程刚登记同 (circuit, type) → 释放新建资源，复用既有实例
        if (!_circuitConsumersByKey.TryAdd(key, consumer))
        {
            ReleaseConsumer(consumer);
            return (TaskCardAggregator)_circuitConsumersByKey[key];
        }
        return consumer;
    }

    /// <summary>
    /// 创建消费者实例。EventStreamHandler 需运行时 sessionId 构造，绕过 DI 占位注册
    /// （空 sessionId）按会话工厂直接 new；其余消费者从新建 scope 解析并登记 scope 供释放。
    /// </summary>
    private T CreateConsumer<T>(string sessionId) where T : IStreamConsumer
    {
        if (typeof(T) == typeof(EventStreamHandler))
        {
            // ISessionManager 为 Singleton；临时 scope 仅用于解析该依赖。
            using var temp = _scopeFactory.CreateScope();
            return (T)(object)new EventStreamHandler(
                sessionId, temp.ServiceProvider.GetRequiredService<ISessionManager>());
        }

        var scope = _scopeFactory.CreateScope();
        try
        {
            var consumer = scope.ServiceProvider.GetRequiredService<T>();
            _consumerScopes[consumer] = scope;
            return consumer;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 摘除单个消费者；若该会话订阅已空则释放订阅。
    /// 仅当摘除的是该 (circuit, session) 的主 consumer（会话维度首个登记的 (circuit, session, type)）时
    /// 释放其 scope；辅助/多会话 consumer（如 TaskCardAggregator）摘除子会话或 Rebind 切换
    /// 父会话时只移除订阅、保留实例，否则实例被 Dispose 后无法复用（ObjectDisposedException）。
    /// </summary>
    public void DetachConsumer(string sessionId, IStreamConsumer consumer)
    {
        var removed = false;
        if (_subscriptions.TryGetValue(sessionId, out var sub))
        {
            if (sub.Consumers.TryRemove(consumer, out _))
                removed = true;
            if (sub.Consumers.IsEmpty)
                ReleaseSubscription(sessionId, sub);
        }

        if (!removed)
            return;

        // 仅释放主 consumer 的 scope（含其 circuit 幂等映射）
        if (_consumerCircuit.TryGetValue(consumer, out var circuit)
            && _mainConsumerBySession.TryGetValue((circuit, sessionId), out var main)
            && ReferenceEquals(main, consumer))
        {
            ReleaseConsumer(consumer);
        }
    }

    /// <summary>
    /// 释放该 circuit 下全部 consumer 的 scope 并摘除其全部订阅（circuit 关闭时调用）。
    /// 依据 <see cref="_consumerCircuit"/> 收集该 circuit 的全部 consumer（含已订阅多会话的辅助
    /// consumer），从所有订阅中摘除并统一释放一次，确保跨会话挂载的实例不残留。
    /// </summary>
    public void DetachAllForCircuit(string circuitId)
    {
        var circuitConsumers = _consumerCircuit
            .Where(kv => kv.Value == circuitId)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var consumer in circuitConsumers)
        {
            foreach (var (sessionId, sub) in _subscriptions.ToArray())
            {
                if (sub.Consumers.TryRemove(consumer, out _))
                {
                    if (sub.Consumers.IsEmpty)
                        ReleaseSubscription(sessionId, sub);
                }
            }
            ReleaseConsumer(consumer);
        }

        // 兜底：清理仅 GetOrCreate 未 Attach 或遗漏的该 circuit consumer
        foreach (var (consumer, _) in _consumerScopes.ToArray())
        {
            if (_consumerCircuit.TryGetValue(consumer, out var c) && c == circuitId)
                ReleaseConsumer(consumer);
        }
    }

    /// <summary>
    /// 释放消费者资源：scope + circuit/幂等/主槽映射（不可再复用）。
    /// I3：释放 scope 前若 consumer 为 IDisposable，先调用其 Dispose（尽力而为，try/catch），
    /// 使 TaskCardAggregator 得以落盘防抖窗口内未持久化的 TaskSteps。
    /// </summary>
    private void ReleaseConsumer(IStreamConsumer consumer)
    {
        _consumerCircuit.TryRemove(consumer, out _);

        if (_consumerScopes.TryRemove(consumer, out var scope))
        {
            try
            {
                // I3：先 flush 再释放锁（scope.Dispose 会连带释放 Scoped 消费者的锁资源）
                if (consumer is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放消费者资源前 flush 失败: {Type}", consumer.GetType().Name);
            }

            try
            {
                scope.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放消费者 scope 失败: {Type}", consumer.GetType().Name);
            }
        }

        foreach (var (key, main) in _mainConsumerBySession.ToArray())
        {
            if (ReferenceEquals(main, consumer))
                _mainConsumerBySession.TryRemove(key, out _);
        }

        foreach (var (key, value) in _consumersByKey.ToArray())
        {
            if (ReferenceEquals(value, consumer))
                _consumersByKey.TryRemove(key, out _);
        }

        foreach (var (key, value) in _circuitConsumersByKey.ToArray())
        {
            if (ReferenceEquals(value, consumer))
                _circuitConsumersByKey.TryRemove(key, out _);
        }
    }

    private async Task ConsumeLoopAsync(
        string sessionId, SessionSubscription mySub, CancellationToken ct,
        HashSet<IMessageEvent> skipSet, int generation)
    {
        try
        {
            await foreach (var evt in _orchestrator.SubscribeEvents(sessionId, ct))
            {
                if (skipSet.Contains(evt))
                    continue;
                Broadcast(sessionId, evt);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消：不视为异常
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "事件流消费循环异常终止: {SessionId}", sessionId);
        }
        finally
        {
            // I1：捕获创建时的 mySub，仅当本订阅仍是当前订阅时才处理，
            // 避免旧 loop 结束与新 Attach 竞态时误广播 OnStreamEnd / 误移除新订阅。
            if (_subscriptions.TryGetValue(sessionId, out var current)
                && ReferenceEquals(current, mySub))
            {
                var isCurrentGeneration = false;
                lock (mySub)
                {
                    // I1：仅当本 loop 仍是当前代（未被新 Attach 重建的 loop 替换）时才管理订阅状态，
                    // 避免旧 loop 收尾时把新 loop 的 Loop/Cts 引用清空
                    if (mySub.LoopGeneration == generation)
                    {
                        isCurrentGeneration = true;
                        if (mySub.Consumers.IsEmpty)
                        {
                            // 消费者已全部摘除：释放订阅
                            ReleaseSubscription(sessionId, mySub);
                        }
                        else
                        {
                            // 流已结束但仍有消费者：清空 loop 引用（并释放其 cts），允许后续 Attach 重建新 loop
                            mySub.Loop = null;
                            var oldCts = mySub.Cts;
                            mySub.Cts = null;
                            oldCts?.Dispose();
                        }
                    }
                }

                // 锁外广播流结束（避免消费者回调内再进 Router 造成死锁）
                if (isCurrentGeneration)
                {
                    foreach (var c in mySub.Consumers.Keys.ToList())
                        SafeInvoke(c, null);
                }
            }
        }
    }

    private void Broadcast(string sessionId, IMessageEvent evt)
    {
        if (_disposed || !_subscriptions.TryGetValue(sessionId, out var sub))
            return;
        foreach (var c in sub.Consumers.Keys.ToList())
            SafeInvoke(c, evt);
    }

    private static void SafeInvoke(IStreamConsumer consumer, IMessageEvent? evt)
    {
        try
        {
            if (evt is null)
                consumer.OnStreamEnd();
            else
                consumer.OnEvent(evt);
        }
        catch
        {
            // 消费者异常隔离：不中断广播
        }
    }

    private void ReleaseSubscription(string sessionId, SessionSubscription sub)
    {
        if (!_subscriptions.TryRemove(sessionId, out _))
            return;

        var cts = sub.Cts;
        sub.Cts = null;
        sub.Loop = null;
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略
            }
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var (sessionId, sub) in _subscriptions.ToArray())
            ReleaseSubscription(sessionId, sub);

        foreach (var scope in _consumerScopes.Values.ToArray())
        {
            try
            {
                scope.Dispose();
            }
            catch
            {
                // 释放失败不阻断整体清理
            }
        }
        _consumerScopes.Clear();
        _consumerCircuit.Clear();
        _consumersByKey.Clear();
        _circuitConsumersByKey.Clear();
        _mainConsumerBySession.Clear();
    }

    private sealed class SessionSubscription
    {
        /// <summary>消费者集合（用字典实现幂等 add/remove）</summary>
        public ConcurrentDictionary<IStreamConsumer, byte> Consumers { get; } = new();

        /// <summary>会话订阅 Loop 任务</summary>
        public Task? Loop { get; set; }

        /// <summary>Loop 取消令牌源</summary>
        public CancellationTokenSource? Cts { get; set; }

        /// <summary>Loop 代次（I1：重启订阅时递增，旧 loop 收尾据此避免清空新 loop 状态）</summary>
        public int LoopGeneration { get; set; }
    }
}
