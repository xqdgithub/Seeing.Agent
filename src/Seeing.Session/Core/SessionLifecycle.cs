using Microsoft.Extensions.Logging;

namespace Seeing.Session.Core
{
    /// <summary>
    /// 封装 ISessionManager 的生命周期方法并集成 SessionEventPublisher
    /// </summary>
    public class SessionLifecycle : ISessionLifecycle
    {
        private readonly ISessionManager _sessionManager;
        private readonly ISessionEventPublisher _eventPublisher;
        private readonly ILogger<SessionLifecycle>? _logger;

        /// <summary>
        /// 创建 SessionLifecycle 实例
        /// </summary>
        /// <param name="sessionManager">会话管理器</param>
        /// <param name="eventPublisher">事件发布器</param>
        /// <param name="logger">日志（可选）</param>
        public SessionLifecycle(
            ISessionManager sessionManager,
            ISessionEventPublisher eventPublisher,
            ILogger<SessionLifecycle>? logger = null)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
            _logger = logger;
        }

        /// <summary>
        /// 开始新会话
        /// </summary>
        /// <param name="title">可选的会话标题</param>
        /// <param name="agentId">可选的 Agent ID</param>
        /// <returns>创建的 SessionData</returns>
        public Task<SessionData> BeginSessionAsync(string? title = null, string? agentId = null)
        {
            // 调用 ISessionManager.Create()
            var session = _sessionManager.Create(selectedAgent: agentId);

            // 设置标题（如果有）
            if (!string.IsNullOrEmpty(title))
            {
                session.Title = title;
            }

            // 触发 SessionEventPublisher.Publish(Created)
            _eventPublisher.Publish(new SessionEvent
            {
                SessionId = session.Id,
                Type = SessionEventType.Created,
                Data = session
            });

            _logger?.LogInformation("开始会话: {SessionId}, Title: {Title}, Agent: {Agent}",
                session.Id, title ?? session.Title, agentId ?? "(default)");

            return Task.FromResult(session);
        }

        /// <summary>
        /// 结束会话
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <returns>完成任务的 Task</returns>
        public Task EndSessionAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger?.LogWarning("无法结束会话：sessionId 为空");
                return Task.CompletedTask;
            }

            // 获取会话数据用于事件发布
            var session = _sessionManager.Get(sessionId);

            // 调用 ISessionManager.Delete()
            var deleted = _sessionManager.Delete(sessionId);

            if (deleted)
            {
                // 触发 SessionEventPublisher.Publish(Destroyed)
                _eventPublisher.Publish(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Destroyed,
                    Data = session
                });

                _logger?.LogInformation("结束会话: {SessionId}", sessionId);
            }
            else
            {
                _logger?.LogWarning("会话不存在或已结束: {SessionId}", sessionId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 克隆会话
        /// </summary>
        /// <param name="sourceId">源会话 ID</param>
        /// <param name="newTitle">可选的新标题</param>
        /// <returns>克隆的 SessionData</returns>
        public Task<SessionData> CloneSessionAsync(string sourceId, string? newTitle = null)
        {
            if (string.IsNullOrEmpty(sourceId))
            {
                throw new ArgumentException("源会话 ID 不能为空", nameof(sourceId));
            }

            // 获取源会话
            var source = _sessionManager.Get(sourceId);
            if (source == null)
            {
                throw new InvalidOperationException($"源会话不存在: {sourceId}");
            }

            // 调用 SessionData.Clone()
            var cloned = source.Clone();

            // 分配新 ID 和更新时间戳
            cloned.Id = $"ses_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            cloned.CreatedAt = DateTime.Now;
            cloned.UpdatedAt = DateTime.Now;
            cloned.LastActiveAt = DateTime.Now;
            cloned.Status = SessionStatus.Created;

            // 设置新标题（如果有）
            if (!string.IsNullOrEmpty(newTitle))
            {
                cloned.Title = newTitle;
            }

            // 注册到 ISessionManager 缓存
            _sessionManager.Register(cloned);

            // 触发 SessionEventPublisher.Publish(Created)
            _eventPublisher.Publish(new SessionEvent
            {
                SessionId = cloned.Id,
                Type = SessionEventType.Created,
                Data = cloned
            });

            _logger?.LogInformation("克隆会话: {SourceId} -> {ClonedId}, Title: {Title}",
                sourceId, cloned.Id, cloned.Title);

            return Task.FromResult(cloned);
        }
    }
}