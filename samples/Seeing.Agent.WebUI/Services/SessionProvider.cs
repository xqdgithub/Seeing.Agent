using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// Session 统一服务入口 - 封装 SessionManager + ISessionEventPublisher + ISessionLifecycle + ISessionStore
    /// <para>
    /// 为 WebUI 提供简化的 API，内部维护会话列表缓存并响应事件更新。
    /// 启动时从持久化存储加载历史会话。
    /// </para>
    /// </summary>
    public class SessionProvider : IDisposable
    {
        private readonly SessionManager _sessionManager;
        private readonly ISessionEventPublisher _eventPublisher;
        private readonly ISessionLifecycle _sessionLifecycle;
        private readonly ISessionStore _store;
        private readonly ILogger<SessionProvider>? _logger;

        /// <summary>
        /// 内部会话列表缓存（从 SessionManager.List() 初始化，响应事件自动更新）
        /// </summary>
        private List<SessionData> _sessionList = new();

        /// <summary>
        /// 当前活动会话
        /// </summary>
        public SessionData? CurrentSession { get; private set; }

        /// <summary>
        /// 当前会话 ID
        /// </summary>
        public string CurrentSessionId => CurrentSession?.Id ?? string.Empty;

        /// <summary>
        /// 会话列表变更事件（通知 UI 重新渲染）
        /// </summary>
        public event Action? OnSessionListChanged;

        /// <summary>
        /// 当前会话变更事件
        /// </summary>
        public event Action? OnCurrentSessionChanged;

        /// <summary>
        /// 事件订阅取消令牌
        /// </summary>
        private IDisposable? _eventSubscription;

        public SessionProvider(
            SessionManager sessionManager,
            ISessionEventPublisher eventPublisher,
            ISessionLifecycle sessionLifecycle,
            ISessionStore store,
            ILogger<SessionProvider>? logger = null)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
            _sessionLifecycle = sessionLifecycle ?? throw new ArgumentNullException(nameof(sessionLifecycle));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;

            // 订阅会话事件
            SubscribeToEvents();
        }

        /// <summary>
        /// 订阅会话事件，自动更新缓存
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventSubscription = _eventPublisher.Events.Subscribe(new SessionEventObserver(this));
        }

        /// <summary>
        /// 初始化会话列表（从持久化存储加载历史会话）
        /// </summary>
        public async Task InitializeSessionListAsync()
        {
            try
            {
                // 从存储加载所有历史会话并注册到 SessionManager
                var sessions = await _store.ListAsync();
                await foreach (var session in sessions)
                {
                    _sessionManager.Register(session);
                }

                // 从内存缓存获取列表
                _sessionList = _sessionManager.List().ToList();
                _logger?.LogInformation("初始化会话列表完成，共加载 {Count} 个历史会话", _sessionList.Count);
                OnSessionListChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "初始化会话列表失败");
                _sessionList = new List<SessionData>();
                OnSessionListChanged?.Invoke();
            }
        }

        /// <summary>
        /// 获取会话列表（按更新时间倒序）
        /// </summary>
        public IReadOnlyList<SessionData> GetSessionList()
        {
            return _sessionList.OrderByDescending(s => s.UpdatedAt).ToList();
        }

        /// <summary>
        /// 创建新会话
        /// </summary>
        /// <param name="title">可选标题</param>
        /// <param name="agentId">可选 Agent ID</param>
        /// <returns>创建的 SessionData</returns>
        public async Task<SessionData> CreateSessionAsync(string? title = null, string? agentId = null)
        {
            var session = await _sessionLifecycle.BeginSessionAsync(title, agentId);

            // 注册到 SessionManager（确保可以被 LoadAsync 加载）
            _sessionManager.Register(session);

            // 保存到存储
            await _sessionManager.SaveAsync(session.Id);

            // 添加到列表缓存
            _sessionList.Insert(0, session);

            // 设置为当前会话
            CurrentSession = session;
            OnCurrentSessionChanged?.Invoke();
            OnSessionListChanged?.Invoke();

            _logger?.LogInformation("创建新会话: {SessionId}, Title: {Title}", session.Id, session.Title);
            return session;
        }

        /// <summary>
        /// 加载指定会话
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <returns>加载的 SessionData，如果不存在返回 null</returns>
        public async Task<SessionData?> LoadSessionAsync(string sessionId)
        {
            var session = await _sessionManager.LoadAsync(sessionId);
            if (session != null)
            {
                CurrentSession = session;
                OnCurrentSessionChanged?.Invoke();
                _logger?.LogInformation("加载会话: {SessionId}", sessionId);
            }
            else
            {
                _logger?.LogWarning("会话不存在: {SessionId}", sessionId);
            }
            return session;
        }

        /// <summary>
        /// 保存当前会话
        /// </summary>
        public async Task SaveCurrentSessionAsync()
        {
            if (CurrentSession != null)
            {
                await _sessionManager.SaveAsync(CurrentSession.Id);
                _logger?.LogInformation("保存会话: {SessionId}", CurrentSession.Id);
            }
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        public async Task DeleteSessionAsync(string sessionId)
        {
            await _sessionLifecycle.EndSessionAsync(sessionId);

            // 如果删除的是当前会话，清除当前会话
            if (CurrentSession?.Id == sessionId)
            {
                CurrentSession = null;
                OnCurrentSessionChanged?.Invoke();
            }

            _logger?.LogInformation("删除会话: {SessionId}", sessionId);
        }

        /// <summary>
        /// 分支会话（克隆）
        /// </summary>
        /// <param name="sourceId">源会话 ID</param>
        /// <param name="newTitle">可选新标题</param>
        /// <returns>克隆的 SessionData</returns>
        public async Task<SessionData> BranchSessionAsync(string sourceId, string? newTitle = null)
        {
            var clonedSession = await _sessionLifecycle.CloneSessionAsync(sourceId, newTitle);

            // 设置为当前会话
            CurrentSession = clonedSession;
            OnCurrentSessionChanged?.Invoke();

            _logger?.LogInformation("分支会话: {SourceId} -> {ClonedId}", sourceId, clonedSession.Id);
            return clonedSession;
        }

        /// <summary>
        /// 重命名会话
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="newTitle">新标题</param>
        public async Task RenameSessionAsync(string sessionId, string newTitle)
        {
            var session = await _sessionManager.LoadAsync(sessionId);
            if (session != null)
            {
                session.Title = newTitle;
                await _sessionManager.SaveAsync(sessionId);

                // 更新缓存中的标题
                var cachedSession = _sessionList.FirstOrDefault(s => s.Id == sessionId);
                if (cachedSession != null)
                {
                    cachedSession.Title = newTitle;
                }

                // 如果是当前会话，更新引用
                if (CurrentSession?.Id == sessionId)
                {
                    CurrentSession.Title = newTitle;
                }

                OnSessionListChanged?.Invoke();
                _logger?.LogInformation("重命名会话: {SessionId} -> {NewTitle}", sessionId, newTitle);
            }
        }

        /// <summary>
        /// 设置当前会话（从已加载的会话列表中选择）
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        public void SetCurrentSession(string sessionId)
        {
            var session = _sessionList.FirstOrDefault(s => s.Id == sessionId);
            if (session != null)
            {
                CurrentSession = session;
                OnCurrentSessionChanged?.Invoke();
            }
        }

        /// <summary>
        /// 内部处理会话事件（由 Observer 调用）
        /// </summary>
        internal void HandleSessionEvent(SessionEvent evt)
        {
            switch (evt.Type)
            {
                case SessionEventType.Created:
                    if (evt.Data != null)
                    {
                        _sessionList.Insert(0, evt.Data);
                        OnSessionListChanged?.Invoke();
                    }
                    break;

                case SessionEventType.Destroyed:
                    var deletedSession = _sessionList.FirstOrDefault(s => s.Id == evt.SessionId);
                    if (deletedSession != null)
                    {
                        _sessionList.Remove(deletedSession);
                        OnSessionListChanged?.Invoke();
                    }
                    break;

                case SessionEventType.Updated:
                case SessionEventType.Saved:
                    // 更新缓存中的会话数据
                    var existingSession = _sessionList.FirstOrDefault(s => s.Id == evt.SessionId);
                    if (existingSession != null && evt.Data != null)
                    {
                        // 更新标题、时间等字段
                        existingSession.Title = evt.Data.Title;
                        existingSession.UpdatedAt = evt.Data.UpdatedAt;
                        // MessageCount 是只读属性，不需要赋值
                        OnSessionListChanged?.Invoke();
                    }
                    break;

                case SessionEventType.Loaded:
                    // 加载事件不更新列表缓存（仅影响当前会话）
                    break;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _eventSubscription?.Dispose();
        }

        /// <summary>
        /// 会话事件观察者（内部类）
        /// </summary>
        private class SessionEventObserver : IObserver<SessionEvent>
        {
            private readonly SessionProvider _provider;

            public SessionEventObserver(SessionProvider provider)
            {
                _provider = provider;
            }

            public void OnNext(SessionEvent value)
            {
                _provider.HandleSessionEvent(value);
            }

            public void OnError(Exception error)
            {
                _provider._logger?.LogError(error, "会话事件订阅发生错误");
            }

            public void OnCompleted()
            {
                // 事件流完成（通常不会发生）
            }
        }
    }
}