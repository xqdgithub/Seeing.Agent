using System;
using System.Collections.Generic;
using System.Threading;
using Seeing.Session.Core;

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
        /// 当前会话数据（由 SessionManager 管理）
        /// </summary>
        public SessionData? CurrentSession { get; set; }

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
            get => CurrentSession?.SelectedAgent ?? "primary";
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

        /// <summary>
        /// 消息列表（从 CurrentSession.Messages 获取）
        /// </summary>
        public List<SessionMessage> Messages => CurrentSession?.Messages ?? new List<SessionMessage>();

        /// <summary>
        /// 是否正在执行
        /// </summary>
        public bool IsExecuting { get; set; }

        /// <summary>
        /// 是否已完成
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated => CurrentSession?.UpdatedAt ?? DateTime.Now;

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
        /// 开始执行，创建新的取消令牌源
        /// </summary>
        public void StartExecution()
        {
            CancelExecution();
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
                _cancellationTokenSource.Dispose();
            }
            _cancellationTokenSource = null;
            IsExecuting = false;
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