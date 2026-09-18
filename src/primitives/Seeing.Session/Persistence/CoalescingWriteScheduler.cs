using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Seeing.Session.Persistence;

/// <summary>
/// 通用合并写调度器：对同一键的多次写入在去抖窗口内合并为一次，并提供显式持久化屏障。
/// <para>
/// 语义依据设计规格 §4：
/// 尾沿去抖（每次新写入重置静默窗口）+ 饥饿保护（自首次变脏起受 <c>maxFlushDelay</c> 约束）、
/// 单飞 flush 循环、按键失败隔离（失败内部重试，不向 <see cref="Enqueue"/> 传播）。
/// </para>
/// </summary>
/// <typeparam name="TKey">键类型（如会话 Id）。</typeparam>
/// <typeparam name="TValue">值类型（如会话快照）。</typeparam>
public sealed class CoalescingWriteScheduler<TKey, TValue> : IDisposable, IAsyncDisposable
    where TKey : notnull
{
    private static readonly TimeSpan BaseRetryBackoff = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _maxFlushDelay;
    private readonly TimeSpan _maxRetryBackoff;
    private readonly TimeSpan _shutdownFlushTimeout;
    private readonly Func<TValue, CancellationToken, Task> _write;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>状态门：保护 pending/firstDirtyAt/inFlight/tombstone 的复合迁移。</summary>
    private readonly object _stateGate = new();

    private readonly ConcurrentDictionary<TKey, TValue> _pending = new();
    private readonly ConcurrentDictionary<TKey, DateTimeOffset> _firstDirtyAt = new();
    private readonly ConcurrentDictionary<TKey, long> _version = new();
    private readonly ConcurrentDictionary<TKey, WaiterGroup> _flushWaiters = new();
    private readonly ConcurrentDictionary<TKey, TaskCompletionSource<bool>> _inFlight = new();
    private readonly ConcurrentDictionary<TKey, DateTimeOffset> _nextRetryAt = new();
    private readonly ConcurrentDictionary<TKey, int> _failureCount = new();
    private readonly ConcurrentDictionary<TKey, byte> _tombstones = new();

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _loopCts = new();
    private readonly Task _loopTask;

    private volatile bool _stopping;
    private int _forceFlushRequests;
    private int _disposed;

    /// <summary>
    /// 创建合并写调度器。
    /// </summary>
    /// <param name="debounceWindow">尾沿去抖窗口。</param>
    /// <param name="maxFlushDelay">自首次变脏起的饥饿保护上限。</param>
    /// <param name="maxRetryBackoff">写失败指数退避上限。</param>
    /// <param name="shutdownFlushTimeout">释放期最终 flush 上限。</param>
    /// <param name="write">实际写入委托：值 + 取消令牌 → 任务。</param>
    /// <param name="logger">可选日志记录器。</param>
    /// <param name="timeProvider">可选时钟（默认为 <see cref="TimeProvider.System"/>）。</param>
    public CoalescingWriteScheduler(
        TimeSpan debounceWindow,
        TimeSpan maxFlushDelay,
        TimeSpan maxRetryBackoff,
        TimeSpan shutdownFlushTimeout,
        Func<TValue, CancellationToken, Task> write,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _debounceWindow = debounceWindow;
        _maxFlushDelay = maxFlushDelay;
        _maxRetryBackoff = maxRetryBackoff;
        _shutdownFlushTimeout = shutdownFlushTimeout;
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _loopTask = Task.Run(RunLoopAsync);
    }

    /// <summary>
    /// 入队一个快照（后写覆盖前写）。立即返回，不阻塞、不抛写入异常。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="snapshot">待写入快照。</param>
    public void Enqueue(TKey key, TValue snapshot)
    {
        ThrowIfDisposed();

        lock (_stateGate)
        {
            // 合法入队清除删除墓碑（支持删除后同 id 重建）
            _tombstones.TryRemove(key, out _);

            var wasDirty = _pending.ContainsKey(key);
            _pending[key] = snapshot;
            _version.AddOrUpdate(key, 1L, static (_, v) => v + 1);

            // 仅在键从非脏转为脏时记录首次脏时刻（饥饿窗口自该时刻起算）
            if (!wasDirty)
                _firstDirtyAt[key] = _timeProvider.GetUtcNow();
        }

        Signal();
    }

    /// <summary>
    /// 有界等待指定键落盘。写失败或超时返回 <c>false</c>（不抛）；调用方取消时抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<bool> TryFlushAsync(TKey key, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var registration = TryRegisterWaiter(key);
        if (registration is null)
            return true;

        var (group, tcs) = registration.Value;

        try
        {
            // 链接令牌：TCS/取消先到时取消计时器，避免每次读路径残留一个 Task.Delay
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(timeout, timeoutCts.Token);
            var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            timeoutCts.Cancel();

            if (completed == tcs.Task)
            {
                await tcs.Task.ConfigureAwait(false);
                return true;
            }

            ct.ThrowIfCancellationRequested();
            RemoveWaiter(key, group, tcs);
            return false;
        }
        catch (OperationCanceledException)
        {
            RemoveWaiter(key, group, tcs);
            throw;
        }
        catch (TimeoutException)
        {
            // 释放期等待者被统一失败：有界语义下返回 false，不抛出
            RemoveWaiter(key, group, tcs);
            return false;
        }
        catch (WriteFailureException)
        {
            // 内层写失败：有界语义下返回 false，不抛出（供读路径保活）
            RemoveWaiter(key, group, tcs);
            return false;
        }
    }

    /// <summary>
    /// 持久化屏障：等待调用时刻该键的最新版本（或更新版本）落盘。受 <paramref name="ct"/> 约束。
    /// </summary>
    public async Task FlushAsync(TKey key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await FlushCoreAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 有界等待所有待写键落盘。任一超时或失败返回 <c>false</c>（不抛）；调用方取消时抛出。
    /// </summary>
    public async Task<bool> TryFlushAllAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var keys = PendingKeys();
        if (keys.Length == 0)
            return true;

        var results = await Task.WhenAll(keys.Select(k => TryFlushAsync(k, timeout, ct))).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (!result)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 持久化屏障：等待所有待写键落盘。受 <paramref name="ct"/> 约束。
    /// </summary>
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await FlushAllCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 丢弃指定键：等待其在途写入完成，移除待写与等待者，并置删除墓碑（下一次合法 <see cref="Enqueue"/> 会清除墓碑）。
    /// </summary>
    /// <remarks>调用方应在返回后执行后端删除，以避免"删除复活"竞态。</remarks>
    public async Task DiscardAsync(TKey key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        Task? inFlight = null;
        lock (_stateGate)
        {
            _tombstones[key] = 0;
            _pending.TryRemove(key, out _);
            _firstDirtyAt.TryRemove(key, out _);
            _nextRetryAt.TryRemove(key, out _);
            _failureCount.TryRemove(key, out _);
            if (_inFlight.TryGetValue(key, out var tcs))
                inFlight = tcs.Task;
        }

        if (inFlight is not null)
            await inFlight.WaitAsync(ct).ConfigureAwait(false);

        lock (_stateGate)
        {
            // 若等待期间发生了合法 Enqueue（Enqueue 会清除墓碑），则新快照必须保留并照常落盘，
            // 不能被本轮 Discard 无条件丢弃；仅当墓碑仍在（无新 Enqueue）时才做二次清理。
            if (!_tombstones.ContainsKey(key))
                return;

            _pending.TryRemove(key, out _);
            _firstDirtyAt.TryRemove(key, out _);
            _inFlight.TryRemove(key, out _);
            if (_flushWaiters.TryRemove(key, out var group))
                group.CompleteAll();
        }
    }

    /// <summary>
    /// 同步释放：触发最终 flush 并最多等待 <c>shutdownFlushTimeout</c>。
    /// </summary>
    /// <remarks>避免在关闭路径使用同步等待异步（无需 <c>GetAwaiter().GetResult()</c>），超时仅记录 Warning。</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            using var cts = new CancellationTokenSource(_shutdownFlushTimeout);
            var flushTask = FlushAllCoreAsync(cts.Token);
            if (!flushTask.Wait(_shutdownFlushTimeout))
                _logger?.LogWarning("同步释放未在 {Timeout} 内完成写回", _shutdownFlushTimeout);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            _logger?.LogWarning("同步释放写回超时（{Timeout}）", _shutdownFlushTimeout);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "同步释放写回失败");
        }

        StopLoopSync();
        FailRemainingWaiters();
        _signal.Dispose();
        _loopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放：触发最终 flush（受 <c>shutdownFlushTimeout</c> 约束）后停止循环。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            using var cts = new CancellationTokenSource(_shutdownFlushTimeout);
            await FlushAllCoreAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("异步释放写回超时（{Timeout}）", _shutdownFlushTimeout);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "异步释放写回失败");
        }

        _stopping = true;
        CancelLoop();
        Signal();
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写回后台循环停止时异常");
        }

        FailRemainingWaiters();
        _signal.Dispose();
        _loopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunLoopAsync()
    {
        var ct = _loopCts.Token;
        try
        {
            while (!_stopping && !ct.IsCancellationRequested)
            {
                if (!_pending.IsEmpty)
                {
                    var wait = ComputeWait();
                    if (wait <= TimeSpan.Zero)
                    {
                        await FlushOnceAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    bool signaled;
                    try
                    {
                        signaled = await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (signaled)
                        continue; // 新信号 → 重算尾沿/饥饿期限（尾沿重置）

                    await FlushOnceAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // 无脏数据：等待入队信号
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写回后台循环异常退出");
        }
    }

    /// <summary>计算距离下一次必须 flush 的等待时长（&lt;=0 表示立即 flush）。</summary>
    private TimeSpan ComputeWait()
    {
        // 屏障请求（FlushAsync/TryFlushAsync）应立即触发一轮 flush
        if (Volatile.Read(ref _forceFlushRequests) > 0)
            return TimeSpan.Zero;

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? earliestFirstDirty = null;
        DateTimeOffset? earliestRetry = null;

        foreach (var key in _pending.Keys)
        {
            if (_nextRetryAt.TryGetValue(key, out var retryAt) && retryAt > now)
            {
                earliestRetry = earliestRetry is { } existing && existing < retryAt ? existing : retryAt;
                continue;
            }

            if (_firstDirtyAt.TryGetValue(key, out var first))
                earliestFirstDirty = earliestFirstDirty is { } existing && existing < first ? existing : first;
            else
                earliestFirstDirty = now;
        }

        DateTimeOffset? waitUntil = null;
        if (earliestFirstDirty is { } firstDirty)
        {
            var quietDeadline = now + _debounceWindow;
            var hardDeadline = firstDirty + _maxFlushDelay;
            waitUntil = quietDeadline < hardDeadline ? quietDeadline : hardDeadline;
        }

        if (earliestRetry is { } retry)
            waitUntil = waitUntil is { } candidate && candidate < retry ? candidate : retry;

        if (waitUntil is null)
            return TimeSpan.Zero;

        var wait = waitUntil.Value - now;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    /// <summary>执行一轮单飞 flush：按键隔离成功/失败，全程不向调用方传播写入异常。</summary>
    private async Task FlushOnceAsync(CancellationToken ct)
    {
        // 消费本轮的屏障请求；flush 期间新到的请求会保留到下一轮
        Interlocked.Exchange(ref _forceFlushRequests, 0);

        var batch = new List<KeyValuePair<TKey, TValue>>();
        lock (_stateGate)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var kv in _pending)
            {
                if (_tombstones.ContainsKey(kv.Key))
                    continue;
                if (_nextRetryAt.TryGetValue(kv.Key, out var retryAt) && retryAt > now)
                    continue;

                batch.Add(kv);
                _inFlight[kv.Key] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (batch.Count == 0)
            return;

        foreach (var kv in batch)
        {
            var key = kv.Key;

            bool tombstoned;
            long snapshotVersion;
            lock (_stateGate)
            {
                tombstoned = _tombstones.ContainsKey(key);
                snapshotVersion = _version.TryGetValue(key, out var v) ? v : 0L;
            }

            if (tombstoned || ct.IsCancellationRequested)
            {
                CompleteInFlight(key);
                continue;
            }

            try
            {
                await _write(kv.Value, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CompleteInFlight(key);
                continue;
            }
            catch (Exception ex)
            {
                RecordFailure(key, ex);
                CompleteInFlight(key);
                continue;
            }

            RecordSuccess(key, snapshotVersion);
            CompleteInFlight(key);
        }
    }

    private void RecordSuccess(TKey key, long snapshotVersion)
    {
        lock (_stateGate)
        {
            // 本轮写已成功，清退避与失败计数
            _nextRetryAt.TryRemove(key, out _);
            _failureCount.TryRemove(key, out _);

            var currentVersion = _version.TryGetValue(key, out var v) ? v : 0L;
            // 版本未变且未被丢弃：本次写入即为最新，清理状态并释放等待者；
            // 否则（flush 期间又有 Enqueue）保留 pending，并为新版本重启饥饿窗口，
            // 使尾沿去抖重新生效（否则旧 hardDeadline 已过会立即连刷，退化为逐 Enqueue 写）。
            if (currentVersion == snapshotVersion && !_tombstones.ContainsKey(key))
            {
                _pending.TryRemove(key, out _);
                _firstDirtyAt.TryRemove(key, out _);
                if (_flushWaiters.TryGetValue(key, out var group))
                    group.CompleteAll();
                TryPruneWaiterGroup(key);
            }
            else
            {
                _firstDirtyAt[key] = _timeProvider.GetUtcNow();
            }
        }
    }

    private void RecordFailure(TKey key, Exception ex)
    {
        TimeSpan backoff;
        WaiterGroup? group = null;
        Exception? failure = null;
        lock (_stateGate)
        {
            if (_tombstones.ContainsKey(key))
                return;

            var now = _timeProvider.GetUtcNow();
            _firstDirtyAt[key] = now; // 重算饥饿窗口
            var attempts = _failureCount.AddOrUpdate(key, 1, static (_, c) => c + 1);
            backoff = ComputeBackoff(attempts);
            _nextRetryAt[key] = now + backoff;

            // 持久化写失败：立即以异常释放当前等待者，避免 FlushAsync/FlushAllAsync 永久挂起。
            // 分组保留在字典中（不 RemoveAll），后续 Enqueue/重试仍可复用。
            failure = new WriteFailureException(key, ex);
            _flushWaiters.TryGetValue(key, out group);
        }

        group?.FailAll(failure!);
        _logger?.LogWarning(ex, "写回失败，键 {Key} 将在 {Backoff} 后重试", key, backoff);
    }

    private TimeSpan ComputeBackoff(int attempts)
    {
        var milliseconds = BaseRetryBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempts - 1));
        if (milliseconds > _maxRetryBackoff.TotalMilliseconds)
            milliseconds = _maxRetryBackoff.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void CompleteInFlight(TKey key)
    {
        TaskCompletionSource<bool>? tcs = null;
        lock (_stateGate)
        {
            if (_inFlight.TryGetValue(key, out var existing))
            {
                tcs = existing;
                _inFlight.TryRemove(key, out _);
            }
        }

        tcs?.TrySetResult(true);
    }

    private async Task FlushCoreAsync(TKey key, CancellationToken ct)
    {
        var registration = TryRegisterWaiter(key);
        if (registration is null)
            return;

        var (group, tcs) = registration.Value;
        try
        {
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemoveWaiter(key, group, tcs);
            throw;
        }
    }

    /// <summary>
    /// 在状态门下注册等待者并请求一次 flush：与完成/剪枝互斥，保证已注册等待者必被未来的完成或自身超时触达。
    /// 返回 <c>null</c> 表示键已落盘（无需等待）。
    /// </summary>
    private (WaiterGroup Group, TaskCompletionSource<bool> Tcs)? TryRegisterWaiter(TKey key)
    {
        lock (_stateGate)
        {
            if (IsSettledCore(key))
                return null;

            var group = _flushWaiters.GetOrAdd(key, static _ => new WaiterGroup());
            var tcs = group.Register();
            RequestFlush();
            Signal();
            return (group, tcs);
        }
    }

    /// <summary>在状态门下移除等待者并按需剪枝空分组。</summary>
    private void RemoveWaiter(TKey key, WaiterGroup group, TaskCompletionSource<bool> tcs)
    {
        lock (_stateGate)
        {
            group.Remove(tcs);
            TryPruneWaiterGroup(key);
        }
    }

    /// <summary>剪枝：键已落盘且分组无等待者时移除分组（调用方须持有 <see cref="_stateGate"/>）。</summary>
    private void TryPruneWaiterGroup(TKey key)
    {
        if (_pending.ContainsKey(key))
            return;

        if (_flushWaiters.TryGetValue(key, out var group) && group.IsEmpty)
            _flushWaiters.TryRemove(key, out _);
    }

    private async Task FlushAllCoreAsync(CancellationToken ct)
    {
        while (true)
        {
            var keys = PendingKeys();
            if (keys.Length == 0)
                return;

            await Task.WhenAll(keys.Select(k => FlushCoreAsync(k, ct))).ConfigureAwait(false);
        }
    }

    private void StopLoopSync()
    {
        _stopping = true;
        CancelLoop();
        Signal();
        try
        {
            _loopTask.Wait(_shutdownFlushTimeout);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            // 正常停止
        }
        catch (AggregateException)
        {
            // 循环内部已记录异常，此处忽略
        }
    }

    private void CancelLoop()
    {
        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
    }

    private void FailRemainingWaiters()
    {
        foreach (var key in _flushWaiters.Keys.ToArray())
        {
            if (_flushWaiters.TryRemove(key, out var group))
                group.FailAll(new TimeoutException("写回调度器已释放，仍有未完成的写入"));
        }
    }

    private TKey[] PendingKeys() => _pending.Keys.ToArray();

    // pending 仅在写入成功后移除；因此 pending 为空即表示最新版本已落盘（或被丢弃）。
    private bool IsSettledCore(TKey key) => !_pending.ContainsKey(key);

    private void RequestFlush() => Interlocked.Increment(ref _forceFlushRequests);

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // 释放竞态：忽略
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(CoalescingWriteScheduler<TKey, TValue>));
    }

    /// <summary>
    /// 内层写入失败时用于释放显式等待者的异常；<see cref="FlushAsync"/> 会传播它，
    /// <see cref="TryFlushAsync"/> 则捕获并转为 <c>false</c>。
    /// </summary>
    private sealed class WriteFailureException : Exception
    {
        public WriteFailureException(object key, Exception inner)
            : base($"持久化写入失败: {key}", inner)
        {
        }
    }

    /// <summary>同一键的等待者集合：成功时统一完成，释放或写失败时统一失败。</summary>
    private sealed class WaiterGroup
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _waiters = new();

        /// <summary>是否已无等待者（用于安全剪枝空分组）。</summary>
        public bool IsEmpty
        {
            get
            {
                lock (_gate)
                    return _waiters.Count == 0;
            }
        }

        public TaskCompletionSource<bool> Register()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _waiters.Add(tcs);
            return tcs;
        }

        public void Remove(TaskCompletionSource<bool> tcs)
        {
            lock (_gate)
                _waiters.Remove(tcs);
        }

        public void CompleteAll()
        {
            TaskCompletionSource<bool>[] copy;
            lock (_gate)
            {
                copy = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var tcs in copy)
                tcs.TrySetResult(true);
        }

        public void FailAll(Exception exception)
        {
            TaskCompletionSource<bool>[] copy;
            lock (_gate)
            {
                copy = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var tcs in copy)
                tcs.TrySetException(exception);
        }
    }
}
