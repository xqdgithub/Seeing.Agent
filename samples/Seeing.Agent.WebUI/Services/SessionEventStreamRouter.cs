using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话事件流路由器（Singleton）。
/// 按 sessionId 对 ExecutionEventPublisher 的流只订阅一次（每会话单 Loop），
/// 再将事件广播给该会话的全部消费者；支持引用快照去重（skipSet 按 Loop 创建时构建）
/// 与 replay 补历史。消费者经 IServiceScopeFactory 创建为 Scoped，Router 维护
/// circuit → (scope, consumer) 映射，circuit 关闭时统一释放。
/// </summary>
public sealed class SessionEventStreamRouter : IDisposable
{
    private readonly IChatOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionEventStreamRouter> _logger;

    private readonly ConcurrentDictionary<string, SessionSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<IStreamConsumer, string> _consumerCircuit = new();
    private readonly ConcurrentDictionary<(string circuit, string session), IServiceScope> _scopes = new();
    private readonly ConcurrentDictionary<string, (string circuit, IStreamConsumer consumer)> _consumerBySession = new();

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
    /// 按 circuit 创建 Scoped 消费者并登记映射。幂等：同会话已存在 consumer 时直接复用
    /// （不新建 scope），避免同 circuit 重访会话导致 scope 泄漏。调用方随后需自行 AttachConsumer。
    /// </summary>
    public T GetOrCreateConsumer<T>(string sessionId, string circuitId) where T : IStreamConsumer
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 幂等：同会话已有 consumer 时复用（不新建 scope）
        if (_consumerBySession.TryGetValue(sessionId, out var existing))
            return (T)existing.consumer;

        var scope = _scopeFactory.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<T>();
        _consumerCircuit[consumer] = circuitId;

        // 竞争窗口：另一线程刚登记同会话 → 释放新建 scope，复用既有实例
        if (!_consumerBySession.TryAdd(sessionId, (circuitId, consumer)))
        {
            try
            {
                scope.Dispose();
            }
            catch
            {
                // 释放失败不阻断
            }
            return (T)_consumerBySession[sessionId].consumer;
        }

        // 极端情况：同 (circuit, session) 已存在不同 scope → 先释放旧 scope 再覆盖
        if (_scopes.TryRemove((circuitId, sessionId), out var oldScope))
        {
            try
            {
                oldScope.Dispose();
            }
            catch
            {
                // 释放失败不阻断
            }
        }
        _scopes[(circuitId, sessionId)] = scope;
        return consumer;
    }

    /// <summary>
    /// 摘除单个消费者；若该会话订阅已空则释放订阅。
    /// 仅当摘除的是该 consumer 的主会话（GetOrCreateConsumer 登记的 (circuit, session)）时释放 scope。
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

        // 仅释放主会话 scope：多会话 consumer（如 TaskCardAggregator）摘除子会话时
        // 不得移除其 circuit 映射，否则父会话 scope 将无法在关闭/摘除时释放（泄漏）。
        if (_consumerBySession.TryGetValue(sessionId, out var entry)
            && ReferenceEquals(entry.consumer, consumer))
        {
            _consumerBySession.TryRemove(sessionId, out _);
            _consumerCircuit.TryRemove(consumer, out _);
            ReleaseScope(entry.circuit, sessionId);
        }
    }

    /// <summary>
    /// 释放该 circuit 下全部 scope 并摘除其消费者（circuit 关闭时调用）。
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
                    _consumerCircuit.TryRemove(consumer, out _);
                }
            }

            if (sub.Consumers.IsEmpty)
                ReleaseSubscription(sessionId, sub);
        }

        foreach (var key in _scopes.Keys.ToArray())
        {
            if (key.circuit == circuitId)
                ReleaseScope(key.circuit, key.session);
        }

        // 反查表同步清理
        foreach (var (sessionId, entry) in _consumerBySession.ToArray())
        {
            if (entry.circuit == circuitId)
                _consumerBySession.TryRemove(sessionId, out _);
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

    private void ReleaseScope(string circuitId, string sessionId)
    {
        if (_scopes.TryRemove((circuitId, sessionId), out var scope))
        {
            try
            {
                scope.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放会话 scope 失败: {CircuitId}/{SessionId}", circuitId, sessionId);
            }
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

        foreach (var scope in _scopes.Values.ToArray())
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
        _scopes.Clear();
        _consumerCircuit.Clear();
        _consumerBySession.Clear();
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
