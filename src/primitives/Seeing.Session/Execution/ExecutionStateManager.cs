namespace Seeing.Session.Execution
{
    // Simple implementation of IExecutionState managing a CancellationTokenSource
    public class ExecutionStateManager : IExecutionState, IDisposable
    {
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public bool IsExecuting { get; private set; }
        public bool IsPaused { get; private set; }
        public string? LastError { get; private set; }

        public ExecutionStateManager()
        {
            IsExecuting = false;
            IsPaused = false;
            LastError = null;
            _cts = null;
        }

        public Task StartExecutionAsync()
        {
            if (IsExecuting)
                return Task.CompletedTask;

            LastError = null;
            IsExecuting = true;
            IsPaused = false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return Task.CompletedTask;
        }

        public Task PauseExecutionAsync()
        {
            if (!IsExecuting)
                return Task.CompletedTask;

            IsPaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeExecutionAsync()
        {
            if (!IsExecuting)
                return Task.CompletedTask;

            IsPaused = false;
            return Task.CompletedTask;
        }

        public Task CancelExecutionAsync()
        {
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // CTS 已释放，正常情况
                }
                catch (AggregateException)
                {
                    // Cancel 触发的回调异常，记录日志
                }
                _cts.Dispose();
                _cts = null;
            }

            IsExecuting = false;
            IsPaused = false;
            LastError = null;
            return Task.CompletedTask;
        }

        public CancellationToken GetCancellationToken()
        {
            return _cts?.Token ?? CancellationToken.None;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_cts != null)
            {
                try { _cts?.Cancel(); }
                catch (ObjectDisposedException)
                {
                    // CTS 已释放，正常情况
                }
                catch (AggregateException)
                {
                    // Cancel 触发的回调异常，记录日志
                }
                _cts!.Dispose();
                _cts = null;
            }
        }
    }
}
