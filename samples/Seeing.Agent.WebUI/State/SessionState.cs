using Seeing.Agent.App.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;
using Seeing.Agent.TokenBudget.Api.Responses;

namespace Seeing.Agent.WebUI.State
{
    /// <summary>
    /// 会话 UI 状态管理类 - 仅管理 UI 相关状态（执行状态、取消令牌等）
    /// <para>
    /// 会话数据（消息、Agent/Model 选择）由 SessionManager + SessionData 统一管理
    /// </para>
    /// </summary>
    public class SessionState
    {
        /// <summary>
        /// 执行锁 - 确保只有一个任务在执行
        /// </summary>
        private readonly SemaphoreSlim _executionLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 当前会话数据（由 SessionManager 管理）
        /// </summary>
        public SessionData? CurrentSession { get; set; }

        /// <summary>
        /// 当前 Todo 列表（由 todowrite 工具更新）
        /// </summary>
        public TodoListViewModel? CurrentTodoList { get; set; }

        /// <summary>
        /// Todo 列表更新事件
        /// </summary>
        public event Action? OnTodoListChanged;

        /// <summary>
        /// 更新 Todo 列表
        /// </summary>
        public void UpdateTodoList(TodoListViewModel todoList)
        {
            CurrentTodoList = todoList;
            OnTodoListChanged?.Invoke();
        }

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

        /// <summary>
        /// 当前选中的 Model 所属 Provider ID（从 CurrentSession 一级字段获取）
        /// </summary>
        public string SelectedModelProvider
        {
            get => CurrentSession?.SelectedModelProvider ?? "";
            set
            {
                if (CurrentSession != null)
                    CurrentSession.SelectedModelProvider = value;
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
        public List<SessionMessage> Messages => CurrentSession?.Messages ?? new List<SessionMessage>();

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

        #region 执行状态（来自 ExecutionJobService）

        /// <summary>
        /// 当前执行状态（来自 ExecutionJobService）
        /// </summary>
        public ExecutionStatus? ExecutionStatus { get; set; }

        /// <summary>
        /// 当前队列位置（0 = 正在执行，>0 = 排队中）
        /// </summary>
        public int QueuePosition { get; set; }

        /// <summary>
        /// 当前执行 ID
        /// </summary>
        public string? CurrentExecutionId { get; set; }

        /// <summary>
        /// 是否有活跃执行（Running 或 Pending）
        /// </summary>
        public bool HasActiveExecution => ExecutionStatus == global::Seeing.Agent.App.Execution.ExecutionStatus.Running ||
                                          ExecutionStatus == global::Seeing.Agent.App.Execution.ExecutionStatus.Pending;

        /// <summary>
        /// 是否在排队中
        /// </summary>
        public bool IsQueued => ExecutionStatus == global::Seeing.Agent.App.Execution.ExecutionStatus.Queued;

        #endregion

        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// 获取当前取消令牌
        /// </summary>
        public CancellationToken CancellationToken =>
            _cancellationTokenSource?.Token ?? CancellationToken.None;

        /// <summary>
        /// 获取取消令牌源（供 UI 绑定）
        /// </summary>
        public CancellationTokenSource? CancellationTokenSource => _cancellationTokenSource;

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

            // 取消之前的执行（如果有）
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }

            _cancellationTokenSource = new CancellationTokenSource();
            IsExecuting = true;
            IsCompleted = false;
            return true;
        }

        /// <summary>
        /// 开始执行，创建新的取消令牌源（会取消之前的执行）
        /// </summary>
        public void StartExecution()
        {
            // 等待获取锁
            _executionLock.Wait();

            // 取消之前的执行
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
            _cancellationTokenSource = new CancellationTokenSource();
            IsExecuting = true;
            IsCompleted = false;
        }

        /// <summary>
        /// 取消当前执行
        /// </summary>
        public void CancelExecution()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
            IsExecuting = false;

            // 释放锁
            if (_executionLock.CurrentCount == 0)
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// 完成执行
        /// </summary>
        public void CompleteExecution()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Dispose();
            }
            _cancellationTokenSource = null;
            IsExecuting = false;
            IsCompleted = true;

            // 释放锁
            if (_executionLock.CurrentCount == 0)
            {
                _executionLock.Release();
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
    }
}