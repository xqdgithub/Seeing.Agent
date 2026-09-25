using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 取消单个执行的结果，用于调用方判定终态事件由哪一方发布。
/// <para>
/// 该判定与状态迁移在同一队列锁内计算，保证「已启动 / 未启动」与取消动作原子，
/// 消除取消 / 启动窗口导致的重复终态事件（每执行恰一次终态、Started 先于终态）。
/// </para>
/// </summary>
internal enum ExecutionCancelOutcome
{
    /// <summary>未找到或已终态，未执行取消。</summary>
    NotFound,

    /// <summary>取消了从未启动的项（排队项或尚未 StartAsync 的当前项）；调用方负责发布终态事件。</summary>
    CancelledNotStarted,

    /// <summary>取消了已启动的当前项；终态事件由执行体 finally 统一发布。</summary>
    CancelledStarted
}

/// <summary>
/// 队列状态的锁内快照，供清理线程无竞争地读取。
/// </summary>
internal readonly record struct ExecutionQueueSnapshot(
    bool HasActiveExecution,
    bool HasQueued,
    int QueueLength,
    DateTime LastActiveTime);

/// <summary>
/// Manages execution queue for a single session.
/// Ensures only one execution is active at a time, with others queued.
/// Thread-safe using SemaphoreSlim for async locking.
/// </summary>
internal class SessionExecutionQueue
{
    private ExecutionRecord? _currentExecution;
    private readonly Queue<ExecutionRecord> _pendingQueue = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastActiveTime = DateTime.UtcNow;
    private bool _disposed;

    /// <summary>
    /// Gets the currently executing record, if any.
    /// </summary>
    public ExecutionRecord? CurrentExecution => _currentExecution;

    /// <summary>
    /// Gets whether there is an active execution (running or pending).
    /// </summary>
    public bool HasActiveExecution => _currentExecution != null &&
        (_currentExecution.Status == ExecutionStatus.Running ||
         _currentExecution.Status == ExecutionStatus.Pending);

    /// <summary>
    /// Gets whether there are queued executions.
    /// </summary>
    public bool HasQueued => _pendingQueue.Count > 0;

    /// <summary>
    /// Gets the number of queued executions.
    /// </summary>
    public int QueueLength => _pendingQueue.Count;

    /// <summary>
    /// Gets the last time this queue was active.
    /// </summary>
    public DateTime LastActiveTime => _lastActiveTime;

    /// <summary>
    /// 在队列锁内读取一致的状态快照（活跃 / 排队 / 空闲时长）。
    /// <para>供清理线程使用，消除直接读取 <see cref="_pendingQueue"/>.Count 等字段的数据竞争。</para>
    /// </summary>
    public ExecutionQueueSnapshot GetSnapshot()
    {
        _lock.Wait();
        try
        {
            var current = _currentExecution;
            var hasActive = current != null &&
                (current.Status == ExecutionStatus.Running ||
                 current.Status == ExecutionStatus.Pending);

            return new ExecutionQueueSnapshot(hasActive, _pendingQueue.Count > 0, _pendingQueue.Count, _lastActiveTime);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Submits a new execution request.
    /// If no execution is active, it becomes the current execution.
    /// Otherwise, it is queued.
    /// </summary>
    /// <param name="record">The execution record to submit.</param>
    /// <exception cref="ObjectDisposedException">队列已释放（供 Submit 循环重取新队列）。</exception>
    public async Task SubmitAsync(ExecutionRecord record)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 已释放队列拒绝新入队：调用方（ExecutionJobService.SubmitAsync）据此重取字典中的新队列，
            // 避免记录落入已从字典摘除的孤儿队列而永不执行。
            ObjectDisposedException.ThrowIf(_disposed, this);

            _lastActiveTime = DateTime.UtcNow;

            // 取消令牌绑定执行记录本身：入队即创建（含排队项），使其在排队期间也可被取消。
            record.Cts ??= new CancellationTokenSource();

            if (_currentExecution == null)
            {
                // No active execution, start immediately
                _currentExecution = record;
                record.Status = ExecutionStatus.Pending;
                record.QueuePosition = 0;
            }
            else
            {
                // Queue the execution
                record.Status = ExecutionStatus.Queued;
                record.QueuePosition = _pendingQueue.Count;
                record.QueuedAt = DateTime.UtcNow;
                _pendingQueue.Enqueue(record);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Marks the current execution as started (transition from Pending to Running).
    /// </summary>
    /// <returns>True if the current execution was started, false if none, not startable, or queue disposed.</returns>
    public async Task<bool> StartAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return false;

            if (_currentExecution != null &&
                _currentExecution.Status == ExecutionStatus.Pending)
            {
                _currentExecution.Status = ExecutionStatus.Running;
                // 状态迁移与 StartedAt 写入在同一队列锁内完成：取消判定以同一锁内状态为准，
                // 消除「Running 已置、StartedAt 未写」窗口导致的重复终态事件。
                _currentExecution.StartedAt = DateTime.UtcNow;
                _lastActiveTime = DateTime.UtcNow;
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Completes the current execution and starts the next one if queued.
    /// </summary>
    /// <param name="record">The execution record being completed.</param>
    /// <param name="status">The final status of the execution.</param>
    /// <returns>The next execution to process, if any.</returns>
    public async Task<ExecutionRecord?> CompleteAsync(ExecutionRecord record, ExecutionStatus status)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentExecution?.ExecutionId == record.ExecutionId)
            {
                _currentExecution.Status = status;
                _currentExecution.CompletedAt = DateTime.UtcNow;
                // 记录已终态：释放其专属 CTS
                record.Cts?.Dispose();
                record.Cts = null;

                // Get next execution from queue
                if (_pendingQueue.TryDequeue(out var next))
                {
                    _currentExecution = next;
                    next.Status = ExecutionStatus.Pending;
                    next.QueuePosition = 0;
                    _lastActiveTime = DateTime.UtcNow;
                    return next;
                }
                else
                {
                    _currentExecution = null;
                    return null;
                }
            }

            // 记录已被取消/推进（CancelAsync 已提升下一项）：此处兜底释放其 CTS，避免泄漏
            record.Cts?.Dispose();
            record.Cts = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Cancels an execution (either running or queued).
    /// </summary>
    /// <param name="executionId">The execution ID to cancel.</param>
    /// <returns>
    /// <see cref="ExecutionCancelOutcome.NotFound"/>（未找到/已终态）、
    /// <see cref="ExecutionCancelOutcome.CancelledNotStarted"/>（从未启动，调用方发布终态）或
    /// <see cref="ExecutionCancelOutcome.CancelledStarted"/>（已启动，执行体发布终态）。
    /// </returns>
    public async Task<ExecutionCancelOutcome> CancelAsync(string executionId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check if it's the current execution
            if (_currentExecution?.ExecutionId == executionId)
            {
                if (_currentExecution.Status == ExecutionStatus.Running)
                {
                    CancelCurrentAndAdvance(disposeCts: false);
                    return ExecutionCancelOutcome.CancelledStarted;
                }

                if (_currentExecution.Status == ExecutionStatus.Pending)
                {
                    // 尚未 StartAsync：无人持有该令牌，可安全释放其 CTS
                    CancelCurrentAndAdvance(disposeCts: true);
                    return ExecutionCancelOutcome.CancelledNotStarted;
                }

                return ExecutionCancelOutcome.NotFound;
            }

            // Check if it's in the queue
            var remaining = new List<ExecutionRecord>();
            var found = false;
            while (_pendingQueue.TryDequeue(out var item))
            {
                if (item.ExecutionId == executionId && !found)
                {
                    // 排队项从未被消费，无人持有其 token，可直接取消并释放
                    item.Cts?.Cancel();
                    item.Cts?.Dispose();
                    item.Cts = null;
                    item.Status = ExecutionStatus.Cancelled;
                    item.CompletedAt = DateTime.UtcNow;
                    found = true;
                }
                else
                {
                    // Update queue position for remaining items
                    if (found)
                    {
                        item.QueuePosition = remaining.Count;
                    }
                    remaining.Add(item);
                }
            }

            // Re-enqueue remaining items
            foreach (var item in remaining)
            {
                _pendingQueue.Enqueue(item);
            }

            return found ? ExecutionCancelOutcome.CancelledNotStarted : ExecutionCancelOutcome.NotFound;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 在当前项上执行取消并推进队列（必须在队列锁内调用）。
    /// </summary>
    private void CancelCurrentAndAdvance(bool disposeCts)
    {
        var current = _currentExecution!;

        if (disposeCts)
        {
            current.Cts?.Dispose();
            current.Cts = null;
        }
        else
        {
            // 只取消本记录专属令牌，不在此 Dispose：执行体可能仍持有该 token，
            // 释放延迟至 CompleteAsync（终态）或队列 Dispose，规避 ObjectDisposedException。
            current.Cts?.Cancel();
        }

        current.Status = ExecutionStatus.Cancelled;
        current.CompletedAt = DateTime.UtcNow;

        // 推进队列：取消当前项后立即释放并启动下一项，避免队列卡死
        if (_pendingQueue.TryDequeue(out var next))
        {
            _currentExecution = next;
            next.Status = ExecutionStatus.Pending;
            next.QueuePosition = 0;
            _lastActiveTime = DateTime.UtcNow;
        }
        else
        {
            _currentExecution = null;
        }
    }

    /// <summary>
    /// Gets all queued executions.
    /// </summary>
    public IReadOnlyList<ExecutionRecord> GetQueuedExecutions()
    {
        _lock.Wait();
        try
        {
            return _pendingQueue.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates queue positions for all queued executions.
    /// </summary>
    public async Task UpdateQueuePositionsAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var index = 0;
            foreach (var item in _pendingQueue)
            {
                item.QueuePosition = index++;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 若队列当前完全空闲，则在同一队列锁内原子地标记为已退休（此后拒绝新入队），返回 true。
    /// 若已存在当前项或排队项（含移除瞬间被并发 Submit 变为活跃），则返回 false，由调用方决定放回或取消。
    /// <para>用于空闲清理：把「是否空闲」与「退休」合并为一次原子判定，消除丢弃带在途项队列的窗口。</para>
    /// </summary>
    public bool TryRetireIfIdle()
    {
        _lock.Wait();
        try
        {
            if (_disposed)
                return true;

            if (_currentExecution != null || _pendingQueue.Count > 0)
                return false;

            _disposed = true;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 释放队列：取消全部未终态执行，但<b>不释放</b>在途执行体的 CTS。
    /// <para>
    /// 在途执行体可能仍持有令牌（Delay / Register），提前释放会触发 ObjectDisposedException；
    /// 其 CTS 由执行体 finally → <see cref="CompleteAsync"/> 负责释放。
    /// 排队项从未被消费，可安全取消并释放。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _lock.Wait();
        try
        {
            if (_disposed)
                return;

            _disposed = true;

            // 在途当前项：仅取消，不释放其 CTS（交由执行体 finally 释放）
            _currentExecution?.Cts?.Cancel();

            foreach (var item in _pendingQueue)
            {
                item.Cts?.Cancel();
                item.Cts?.Dispose();
                item.Cts = null;
                // 排队项随队列释放即进入终态（不会再被执行）；终态事件由调用方负责发布
                item.Status = ExecutionStatus.Cancelled;
                item.CompletedAt = DateTime.UtcNow;
            }
            _pendingQueue.Clear();
        }
        finally
        {
            _lock.Release();
        }

        // 不释放 _lock：SemaphoreSlim 无原生非托管资源；释放后并发的 GetSnapshot / SubmitAsync
        // 会抛 ObjectDisposedException 且无法被幂等处理。改由 _disposed 标志拒绝新入队并保证幂等。
    }
}
