using System.Threading;
using System.Threading.Tasks;

namespace Seeing.Session.Execution
{
    // Simple implementation of IExecutionState managing a CancellationTokenSource
    public class ExecutionStateManager : IExecutionState
    {
        private CancellationTokenSource? _cts;

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
            // If already running, no-op
            if (IsExecuting)
                return Task.CompletedTask;

            LastError = null;
            IsExecuting = true;
            IsPaused = false;
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
            // Cancel any ongoing operation and reset state
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                    // Ignore exceptions from cancellation
                }
                _cts.Dispose();
                _cts = null;
            }

            IsExecuting = false;
            IsPaused = false;
            // Do not swallow existing LastError; clear for fresh runs
            LastError = null;
            return Task.CompletedTask;
        }

        public CancellationToken GetCancellationToken()
        {
            return _cts?.Token ?? CancellationToken.None;
        }
    }
}
