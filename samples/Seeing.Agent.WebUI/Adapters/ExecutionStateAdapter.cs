using System;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.WebUI.State;
using Seeing.Session.Execution;

namespace Seeing.Agent.WebUI.Adapters
{
    /// <summary>
    /// 执行状态适配器 - 将 WebUI 的 SessionState 适配到 IExecutionState 接口
    /// <para>
    /// 负责将 SessionState 的 CancellationTokenSource 管理委托给 IExecutionState
    /// </para>
    /// </summary>
    public class ExecutionStateAdapter : IExecutionState
    {
        private readonly SessionState _sessionState;
        private CancellationTokenSource? _cancellationTokenSource;

        public ExecutionStateAdapter(SessionState sessionState)
        {
            _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        }

        /// <inheritdoc/>
        public bool IsExecuting => _sessionState.IsExecuting;

        /// <inheritdoc/>
        public bool IsPaused => false; // WebUI 不支持暂停

        /// <inheritdoc/>
        public string? LastError { get; private set; }

        /// <inheritdoc/>
        public Task StartExecutionAsync()
        {
            CancelExecutionInternal();
            _cancellationTokenSource = new CancellationTokenSource();
            _sessionState.StartExecution();
            LastError = null;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PauseExecutionAsync()
        {
            // WebUI 不支持暂停功能
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task ResumeExecutionAsync()
        {
            // WebUI 不支持暂停功能
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task CancelExecutionAsync()
        {
            CancelExecutionInternal();
            _sessionState.CancelExecution();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public CancellationToken GetCancellationToken()
        {
            return _cancellationTokenSource?.Token ?? CancellationToken.None;
        }

        /// <summary>
        /// 标记执行完成
        /// </summary>
        public void CompleteExecution()
        {
            _sessionState.CompleteExecution();
            CancelExecutionInternal();
        }

        /// <summary>
        /// 标记执行出错
        /// </summary>
        public void SetError(string error)
        {
            LastError = error;
            CancelExecutionAsync().Wait();
        }

        private void CancelExecutionInternal()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // 忽略已释放异常
                }
            }
            _cancellationTokenSource = null;
        }
    }
}
