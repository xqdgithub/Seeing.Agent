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
/// 每 (circuit, session) 可注册多个不同消费者类型：首个 GetOrCreateConsumer 登记的
/// 类型为该会话「主 consumer」（如渲染 handler），其余为辅助 consumer（如 TaskCardAggregator）。
/// 主 consumer 摘除（页面会话切换/关闭）时释放其 scope；辅助 consumer 摘除子会话或
/// Rebind 切换父会话时仅移除订阅、不释放实例，供后续复用。
/// </para>
/// <para>
/// EventStreamHandler 需运行时 sessionId 构造，GetOrCreateConsumer 对其走「按会话工厂」
/// 直接 new（绕过 DI 占位注册的空 sessionId 实例），不挂 scope。
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

    /// <summary>幂等复用键：(session, consumerType) → consumer（同会话同类型复用实例）</summary>
    private readonly ConcurrentDictionary<(string session, Type type), IStreamConsumer> _consumersByKey = new();

    /// <summary>每会话主 consumer（首个 GetOrCreateConsumer 登记；摘除主 consumer 时释放其 scope）</summary>
    private readonly ConcurrentDictionary<string, IStreamConsumer> _mainConsumerBySession = new();

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
    /// 注册消费者。已注册则幂等跳过；replay=true 时先补发当前缓冲历史。
    /// 首个消费者触发该会话的订阅 Loop（引用快照去重）。
    /// </summary>
    public void AttachConsumer(string sessionId, IStreamConsumer consumer, bool replay = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sub = _subscriptions.GetOrAdd(sessionId, _ => new SessionSubscription());
        if (!sub.Consumers.TryAdd(consumer, 0) && sub.Loop != null)
            return;

        var buffered = _orchestrator.GetBufferedEvents(sessionId) ?? Array.Empty<IMessageEvent>();

        if (replay)
        {
            foreach (var evt in buffered)
                SafeInvoke(consumer, evt);
        }

        lock (sub)
        {
            if (sub.Loop == null)
            {
                var skipSet = new HashSet<IMessageEvent>(buffered, ReferenceEqualityComparer.Instance);
                var cts = new CancellationTokenSource();
                sub.Cts = cts;
                sub.Loop = ConsumeLoopAsync(sessionId, sub, cts.Token, skipSet);
            }
        }
    }

    /// <summary>
    /// 按 circuit 创建 Scoped 消费者并登记映射。幂等：同 (session, type) 已存在 consumer 时直接复用
    /// （不新建 scope），避免同 circuit 重访会话导致 scope 泄漏。调用方随后需自行 AttachConsumer。
    /// 首个登记的 consumer 为该会话「主 consumer」（DetachConsumer 摘除时释放其 scope）；
    /// EventStreamHandler 走按会话工厂直接构造（sessionId 运行时确定），不挂 scope。
    /// </summary>
    public T GetOrCreateConsumer<T>(string sessionId, string circuitId) where T : IStreamConsumer
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (sessionId, typeof(T));
        if (_consumersByKey.TryGetValue(key, out var existing))
            return (T)existing;

        var consumer = CreateConsumer<T>(sessionId);
        _consumerCircuit[consumer] = circuitId;

        // 主 consumer：每会话首个登记。辅助 consumer（如 TaskCardAggregator）摘除子会话 /
        // Rebind 切换父会话时 Detach 旧父不得释放其实例，因此仅主 consumer 摘除才释放 scope。
        _mainConsumerBySession.TryAdd(sessionId, consumer);

        // 竞争窗口：另一线程刚登记同 (session, type) → 释放新建资源，复用既有实例
        if (!_consumersByKey.TryAdd(key, consumer))
        {
            ReleaseConsumer(consumer);
            return (T)_consumersByKey[key];
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
    /// 仅当摘除的是该会话的主 consumer（GetOrCreateConsumer 首个登记的 (session, type)）时
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
        if (_mainConsumerBySession.TryGetValue(sessionId, out var main)
            && ReferenceEquals(main, consumer))
        {
            ReleaseConsumer(consumer);
        }
    }

    /// <summary>
    /// 释放该 circuit 下全部 consumer 的 scope 并摘除其订阅（circuit 关闭时调用）。
    /// </summary>
    public void DetachAllForCircuit(string circuitId)
    {
        foreach (var (sessionId, sub) in _subscriptions.ToArray())
        {
            foreach (var consumer in sub.Consumers.Keys.ToArray())
            {
                if (_consumerCircuit.TryGetValue(consumer, out var c) && c == circuitId
                    && sub.Consumers.TryRemove(consumer, out _))
                {
                    ReleaseConsumer(consumer);
                }
            }

            if (sub.Consumers.IsEmpty)
                ReleaseSubscription(sessionId, sub);
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
    /// </summary>
    private void ReleaseConsumer(IStreamConsumer consumer)
    {
        _consumerCircuit.TryRemove(consumer, out _);
        if (_consumerScopes.TryRemove(consumer, out var scope))
        {
            try
            {
                scope.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放消费者 scope 失败: {Type}", consumer.GetType().Name);
            }
        }

        foreach (var (sessionId, main) in _mainConsumerBySession.ToArray())
        {
            if (ReferenceEquals(main, consumer))
                _mainConsumerBySession.TryRemove(sessionId, out _);
        }

        foreach (var (key, value) in _consumersByKey.ToArray())
        {
            if (ReferenceEquals(value, consumer))
                _consumersByKey.TryRemove(key, out _);
        }
    }

    private async Task ConsumeLoopAsync(
        string sessionId, SessionSubscription mySub, CancellationToken ct, HashSet<IMessageEvent> skipSet)
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
                foreach (var c in mySub.Consumers.Keys.ToList())
                    SafeInvoke(c, null);

                lock (mySub)
                {
                    if (mySub.Consumers.IsEmpty)
                    {
                        // 消费者已全部摘除：释放订阅
                        ReleaseSubscription(sessionId, mySub);
                    }
                    else
                    {
                        // 流已结束但仍有消费者：清空 loop 引用，允许后续 Attach 重建新 loop
                        mySub.Loop = null;
                        mySub.Cts = null;
                    }
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
    }
}
