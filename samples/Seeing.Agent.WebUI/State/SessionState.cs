using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.State
{
    /// <summary>
    /// 会话 UI 状态管理类 - 仅管理 UI 相关状态（执行状态、取消令牌等）
    /// <para>
    /// 会话数据（消息、Agent/Model 选择）由 SessionManager + SessionData 统一管理
    /// </para>
    /// </summary>
    public class SessionState : IDisposable
    {
        /// <summary>
        /// 执行锁 - 确保只有一个任务在执行
        /// </summary>
        private readonly SemaphoreSlim _executionLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 取消令牌源读写互斥门。
        /// <para>
        /// 提交（<see cref="TryStartExecution"/>）、取消（<see cref="CancelExecution"/>）、
        /// 完成（<see cref="CompleteExecution"/>）、会话切换（<see cref="ResetExecutionState"/>）、
        /// 服务端恢复（<see cref="SyncExecutionStarted"/>）分布在 UI 点击、事件流后台线程、
        /// 参数更新三条线程上，会并发读写同一字段。裸的「读字段 → 判空 → 调用」会因字段被
        /// 并发置空而抛 <see cref="NullReferenceException"/>，故统一走本门串行化。
        /// </para>
        /// </summary>
        private readonly object _tokenGate = new();

        /// <summary>
        /// 是否已释放（防重入）
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// 当前会话数据（由 SessionManager 管理）
        /// </summary>
        public SessionData? CurrentSession { get; set; }

        #region Token Budget 状态

        /// <summary>
        /// 当前 Token Budget 状态
        /// </summary>
        public BudgetStatusResponse BudgetStatus { get; set; } = new()
        {
            MaxTokens = 0,  // 初始为 0，等待模型选择后更新
            CurrentTokens = 0,
            AvailableTokens = 0,
            UsagePercentage = 0,
            Level = "normal",
            Message = null,
            NeedsCompaction = false
        };

        /// <summary>
        /// Budget 状态更新事件
        /// </summary>
        public event Action? OnBudgetStatusChanged;

        /// <summary>
        /// 更新 Budget 状态
        /// </summary>
        public void UpdateBudgetStatus(BudgetStatusResponse status)
        {
            BudgetStatus = status;
            OnBudgetStatusChanged?.Invoke();
        }

        #endregion

        /// <summary>
        /// 会话 ID（从 CurrentSession 获取）
        /// </summary>
        public string SessionId => CurrentSession?.Id ?? Guid.NewGuid().ToString();

        /// <summary>
        /// 会话标题（从 CurrentSession 获取）
        /// </summary>
        public string Title => CurrentSession?.Title ?? "新会话";

        /// <summary>
        /// 当前选中的 Agent ID（从 CurrentSession 一级字段获取）
        /// </summary>
        public string SelectedAgent
        {
            get => CurrentSession?.SelectedAgent ?? string.Empty;
            set
            {
                if (CurrentSession != null)
                    CurrentSession.SelectedAgent = value;
            }
        }

        /// <summary>
        /// 当前选中的 Model ID（从 CurrentSession 一级字段获取）
        /// </summary>
        public string SelectedModel
        {
            get => CurrentSession?.SelectedModel ?? "";
            set
            {
                if (CurrentSession != null)
                    CurrentSession.SelectedModel = value;
            }
        }

        /// <summary>当前选中的思考强度 key（空 = 跟随模型 default / 不传）</summary>
        public string SelectedThinkingEffort
        {
            get => CurrentSession?.SelectedThinkingEffort ?? "";
            set
            {
                if (CurrentSession != null)
                    CurrentSession.SelectedThinkingEffort = value;
            }
        }

        /// <summary>ACP 透传 session mode（如 build / ask）</summary>
        public string SelectedAcpMode
        {
            get => CurrentSession?.SelectedAcpMode ?? string.Empty;
            set
            {
                if (CurrentSession != null)
                    CurrentSession.SelectedAcpMode = value;
            }
        }

        /// <summary>
        /// 消息列表（从 CurrentSession.Messages 获取）
        /// </summary>
        public IReadOnlyList<SessionMessage> Messages => CurrentSession?.Messages ?? new List<SessionMessage>();

        /// <summary>
        /// 是否正在执行
        /// </summary>
        public bool IsExecuting { get; private set; }

        /// <summary>
        /// 是否正在压缩会话
        /// </summary>
        public bool IsCompacting { get; set; }

        /// <summary>
        /// 是否已完成
        /// </summary>
        public bool IsCompleted { get; private set; }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated => CurrentSession?.UpdatedAt ?? DateTime.Now;

        #region 执行状态（来自 SessionRuntime）

        /// <summary>
        /// 当前执行状态（来自 SessionRuntime）
        /// </summary>
        public ExecutionStatus? ExecutionStatus { get; set; }

        /// <summary>
        /// 当前会话未消费输入数（仅 UI 展示）
        /// </summary>
        public int PendingInputCount { get; set; }

        /// <summary>
        /// 当前执行 ID
        /// </summary>
        public string? CurrentExecutionId { get; set; }

        /// <summary>
        /// 是否有活跃执行（Running / Pending，或 UI 已 StartExecution 但尚未拿到后端 Status）
        /// </summary>
        public bool HasActiveExecution =>
            IsExecuting
            || ExecutionStatus == global::Seeing.Agent.Abstractions.Execution.ExecutionStatus.Running
            || ExecutionStatus == global::Seeing.Agent.Abstractions.Execution.ExecutionStatus.Pending;

        #endregion

        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// 获取当前取消令牌（无令牌时返回 <see cref="CancellationToken.None"/>）。
        /// </summary>
        public CancellationToken CancellationToken
        {
            get
            {
                lock (_tokenGate)
                    return _cancellationTokenSource?.Token ?? CancellationToken.None;
            }
        }

        /// <summary>
        /// 获取取消令牌源（供 UI 绑定）。
        /// </summary>
        public CancellationTokenSource? CancellationTokenSource
        {
            get
            {
                lock (_tokenGate)
                    return _cancellationTokenSource;
            }
        }

        /// <summary>
        /// 尝试开始执行 - 如果已有任务执行则返回 false
        /// </summary>
        public bool TryStartExecution()
        {
            // 尝试获取锁，如果已有任务执行则立即返回 false
            if (!_executionLock.Wait(0))
            {
                return false;
            }

            lock (_tokenGate)
            {
                // 取消之前的执行（如果有）
                ReplaceCancellationTokenSourceLocked(cancelPrevious: true);
                IsExecuting = true;
                IsCompleted = false;
            }

            return true;
        }

        /// <summary>
        /// 开始执行，创建新的取消令牌源（会取消之前的执行）
        /// </summary>
        public void StartExecution()
        {
            // 等待获取锁
            _executionLock.Wait();

            lock (_tokenGate)
            {
                // 取消之前的执行
                ReplaceCancellationTokenSourceLocked(cancelPrevious: true);
                IsExecuting = true;
                IsCompleted = false;
            }
        }

        /// <summary>
        /// 取消当前执行
        /// </summary>
        public void CancelExecution()
        {
            if (_disposed)
                return;

            lock (_tokenGate)
            {
                CancelTokenSourceLocked();
                IsExecuting = false;
            }

            ReleaseExecutionLock();
        }

        /// <summary>
        /// 切换会话时重置本地执行态：清空状态字段、取消并释放旧取消令牌、释放执行锁。
        /// <para>
        /// 复用窗口实例切换会话时，若不重置，新会话会继承旧会话的
        /// <see cref="IsExecuting"/> / <see cref="ExecutionStatus"/> / 取消令牌，
        /// 表现为「空闲的 B 显示执行中」且点「取消」会停掉 A。
        /// </para>
        /// </summary>
        public void ResetExecutionState()
        {
            if (_disposed)
                return;

            lock (_tokenGate)
            {
                IsExecuting = false;
                IsCompleted = false;
                ExecutionStatus = null;
                CurrentExecutionId = null;
                PendingInputCount = 0;

                CancelTokenSourceLocked();
            }

            ReleaseExecutionLock();
        }

        /// <summary>
        /// 完成执行
        /// </summary>
        public void CompleteExecution()
        {
            lock (_tokenGate)
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                IsExecuting = false;
                IsCompleted = true;
            }

            ReleaseExecutionLock();
        }

        /// <summary>
        /// 释放执行锁（幂等）。
        /// <para>
        /// <see cref="CancelExecution"/> / <see cref="CompleteExecution"/> /
        /// <see cref="ResetExecutionState"/> 可能并发执行，<c>CurrentCount</c> 的检查与
        /// <see cref="SemaphoreSlim.Release()"/> 之间存在 TOCTOU 窗口，双释放会抛
        /// <see cref="SemaphoreFullException"/> 且无上层兜底（会打崩 circuit）。
        /// 此处以异常兜底保证幂等；正常情况下最多释放到 1，不影响后续 <c>Wait</c>。
        /// </para>
        /// </summary>
        private void ReleaseExecutionLock()
        {
            try
            {
                if (_executionLock.CurrentCount == 0)
                    _executionLock.Release();
            }
            catch (SemaphoreFullException)
            {
                // 并发路径已完成释放：幂等语义，忽略
            }
            catch (ObjectDisposedException)
            {
                // 会话状态已释放：无需再归还
            }
        }

        /// <summary>
        /// 取消并释放当前取消令牌源（调用方必须持有 <see cref="_tokenGate"/>）。
        /// </summary>
        private void CancelTokenSourceLocked()
        {
            var previous = _cancellationTokenSource;
            if (previous is null)
                return;

            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略
            }
            catch (AggregateException)
            {
                // 回调抛错不影响取消失败语义
            }

            previous.Dispose();
            _cancellationTokenSource = null;
        }

        /// <summary>
        /// 替换取消令牌源（调用方必须持有 <see cref="_tokenGate"/>）。
        /// </summary>
        /// <param name="cancelPrevious">是否先取消并释放旧令牌源。</param>
        private void ReplaceCancellationTokenSourceLocked(bool cancelPrevious)
        {
            if (cancelPrevious)
                CancelTokenSourceLocked();

            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// 同步「执行已开始」事件（服务端可能由 idle resume 主动发起新执行，
        /// 此时 UI 未经过 Submit 流程，需据此恢复执行态，否则取消按钮/执行中徽标缺失）。
        /// 已在执行中则仅更新执行 ID，复用现有取消令牌，保持幂等。
        /// </summary>
        /// <param name="executionId">服务端执行 ID</param>
        public void SyncExecutionStarted(string executionId)
        {
            if (_disposed)
                return;

            CurrentExecutionId = executionId;
            ExecutionStatus = global::Seeing.Agent.Abstractions.Execution.ExecutionStatus.Running;

            // 非阻塞尝试获取执行锁（UI 空闲时成功；已执行中则失败，保持既有令牌）
            if (!IsExecuting)
                TryStartExecution();

            // 令牌缺失/已取消时补建，保证取消按钮可用（如取消后服务端 resume 场景）
            lock (_tokenGate)
            {
                if (_cancellationTokenSource is null || _cancellationTokenSource.IsCancellationRequested)
                    ReplaceCancellationTokenSourceLocked(cancelPrevious: true);
            }
        }

        /// <summary>
        /// 设置当前会话
        /// </summary>
        public void SetSession(SessionData session)
        {
            CurrentSession = session;
        }

        /// <summary>
        /// 重置 UI 状态（不清除 Session 数据）
        /// </summary>
        public void ResetUIState()
        {
            CancelExecution();
            IsCompleted = false;
        }

        /// <summary>
        /// 释放执行锁与取消令牌源（窗口自建实例时由 SessionWindow.Dispose 调用；Scoped 实例随 scope 回收）。
        /// 防重入：仅首次释放，避免 Dispose 后并发 CancelExecution 操作已释放的锁。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _executionLock.Dispose();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
}