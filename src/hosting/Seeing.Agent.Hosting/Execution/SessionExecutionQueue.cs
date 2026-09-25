using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Hosting.Execution;

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
    /// Submits a new execution request.
    /// If no execution is active, it becomes the current execution.
    /// Otherwise, it is queued.
    /// </summary>
    /// <param name="record">The execution record to submit.</param>
    public async Task SubmitAsync(ExecutionRecord record)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
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
    /// <returns>True if the current execution was started, false if none or not startable.</returns>
    public async Task<bool> StartAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentExecution != null &&
                _currentExecution.Status == ExecutionStatus.Pending)
            {
                _currentExecution.Status = ExecutionStatus.Running;
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
    /// <returns>True if cancelled, false if not found or already terminal.</returns>
    public async Task<bool> CancelAsync(string executionId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Check if it's the current execution
            if (_currentExecution?.ExecutionId == executionId)
            {
                if (_currentExecution.Status == ExecutionStatus.Running ||
                    _currentExecution.Status == ExecutionStatus.Pending)
                {
                    // 只取消本记录专属令牌，不在此 Dispose：执行体可能仍持有该 token，
                    // 释放延迟至 CompleteAsync（终态）或队列 Dispose，规避 ObjectDisposedException。
                    _currentExecution.Cts?.Cancel();
                    _currentExecution.Status = ExecutionStatus.Cancelled;
                    _currentExecution.CompletedAt = DateTime.UtcNow;

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
                    return true;
                }
                return false;
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

            return found;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all queued executions.
    /// </summary>
    public IReadOnlyList<ExecutionRecord> GetQueuedExecutions()
    {
        return _pendingQueue.ToList();
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
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        // 释放当前项与所有排队项各自的 CTS
        _currentExecution?.Cts?.Dispose();
        foreach (var item in _pendingQueue)
        {
            item.Cts?.Dispose();
        }
        _pendingQueue.Clear();
        _lock.Dispose();
    }
}