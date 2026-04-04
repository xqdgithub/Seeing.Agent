using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using System.Collections.Concurrent;

namespace Seeing.Agent.Sessions
{
    /// <summary>
    /// 会话数据 - 线程安全实现
    /// </summary>
    public class SessionData
    {
        private readonly ConcurrentQueue<ChatMessage> _messages = new();
        private readonly ConcurrentDictionary<string, object> _context = new();

        /// <summary>会话 ID</summary>
        public string SessionId { get; init; } = string.Empty;
        
        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        
        /// <summary>最后活跃时间（原子操作）</summary>
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>当前 Agent（单线程写，多线程读）</summary>
        public IAgent? ActiveAgent { get; set; }

        /// <summary>工作目录</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// 消息列表 - 返回快照
        /// </summary>
        public IReadOnlyList<ChatMessage> Messages => _messages.ToList();

        /// <summary>
        /// 上下文数据 - 返回快照
        /// </summary>
        public IReadOnlyDictionary<string, object> Context => new Dictionary<string, object>(_context);

        /// <summary>
        /// 添加消息 - 线程安全
        /// </summary>
        public void AddMessage(ChatMessage message)
        {
            _messages.Enqueue(message);
            LastActiveAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 设置上下文值 - 线程安全
        /// </summary>
        public void SetContextValue(string key, object value)
        {
            _context[key] = value;
        }

        /// <summary>
        /// 获取上下文值 - 线程安全
        /// </summary>
        public T? GetContextValue<T>(string key)
        {
            if (_context.TryGetValue(key, out var value) && value is T typedValue)
                return typedValue;
            return default;
        }

        /// <summary>
        /// 尝试获取上下文值
        /// </summary>
        public bool TryGetContextValue<T>(string key, out T? value)
        {
            if (_context.TryGetValue(key, out var obj) && obj is T typedValue)
            {
                value = typedValue;
                return true;
            }
            value = default;
            return false;
        }

        /// <summary>
        /// 移除上下文值
        /// </summary>
        public bool RemoveContextValue(string key)
        {
            return _context.TryRemove(key, out _);
        }

        /// <summary>
        /// 获取消息数量
        /// </summary>
        public int MessageCount => _messages.Count;
    }

    /// <summary>
    /// 聊天消息
    /// </summary>
    public class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        /// <summary>可选多模态段（与会话快照一并存储）。</summary>
        public List<ChatContentPart>? Parts { get; set; }
    }

    /// <summary>
    /// 会话管理器 - 管理会话的创建、存储和检索
    /// </summary>
    public class SessionManager : ISessionManager
    {
        private readonly ILogger<SessionManager> _logger;
        private readonly IHookManager _hookManager;
        private readonly ConcurrentDictionary<string, SessionData> _sessions = new();

        public SessionManager(ILogger<SessionManager> logger, IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        /// <summary>
        /// 创建新会话
        /// </summary>
        public async Task<SessionData> CreateSessionAsync(IAgent? agent = null, string? cwd = null)
        {
            var sessionId = GenerateSessionId();
            var session = new SessionData
            {
                SessionId = sessionId,
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
                ActiveAgent = agent,
                WorkingDirectory = cwd
            };

            _sessions[sessionId] = session;
            _logger.LogInformation("创建会话: {SessionId}, Agent: {AgentName}", sessionId, agent?.Name ?? "无");

            // 触发 session.created Hook
            await _hookManager.TriggerAsync(
                HookPoints.SessionCreated,
                new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["agent"] = agent as object,
                    ["cwd"] = cwd ?? string.Empty
                });

            return session;
        }

        /// <summary>
        /// 创建新会话（同步版本，向后兼容）
        /// </summary>
        public SessionData CreateSession(IAgent? agent = null)
        {
            return CreateSessionAsync(agent).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 获取会话
        /// </summary>
        public SessionData? GetSession(string sessionId)
        {
            return string.IsNullOrEmpty(sessionId) ? null : _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        /// <summary>
        /// 获取或创建会话
        /// </summary>
        public SessionData GetOrCreateSession(string? sessionId, IAgent? agent = null)
        {
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var existing))
            {
                existing.LastActiveAt = DateTime.UtcNow;
                return existing;
            }
            return CreateSession(agent);
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            if (_sessions.TryRemove(sessionId, out var session))
            {
                _logger.LogInformation("删除会话: {SessionId}", sessionId);

                // 触发 session.deleted Hook
                await _hookManager.TriggerAsync(
                    HookPoints.SessionDeleted,
                    new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["session"] = session
                    });

                return true;
            }
            return false;
        }

        /// <summary>
        /// 删除会话（同步版本，向后兼容）
        /// </summary>
        public bool DeleteSession(string sessionId)
        {
            return DeleteSessionAsync(sessionId).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 添加消息到会话
        /// </summary>
        public void AddMessage(string sessionId, ChatMessage message)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                _logger.LogWarning("会话不存在: {SessionId}", sessionId);
                return;
            }

            session.AddMessage(message);
        }

        /// <summary>
        /// 添加消息到会话（带 Hook 触发）
        /// </summary>
        public async Task AddMessageAsync(string sessionId, ChatMessage message)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                _logger.LogWarning("会话不存在: {SessionId}", sessionId);
                return;
            }

            session.AddMessage(message);

            // 触发 session.updated Hook
            await _hookManager.TriggerAsync(
                HookPoints.SessionUpdated,
                new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["changeType"] = "message_added",
                    ["changeData"] = message
                });
        }

        /// <summary>
        /// 获取会话消息历史
        /// </summary>
        public IReadOnlyList<ChatMessage> GetMessages(string sessionId)
        {
            return GetSession(sessionId)?.Messages ?? Array.Empty<ChatMessage>();
        }

        /// <summary>
        /// 设置会话上下文值
        /// </summary>
        public void SetContext(string sessionId, string key, object value)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                session.SetContextValue(key, value);
                _logger.LogDebug("设置会话上下文: {SessionId}, Key: {Key}", sessionId, key);
            }
        }

        /// <summary>
        /// 设置会话上下文值（带 Hook 触发）
        /// </summary>
        public async Task SetContextAsync(string sessionId, string key, object value)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                session.SetContextValue(key, value);
                _logger.LogDebug("设置会话上下文: {SessionId}, Key: {Key}", sessionId, key);

                // 触发 session.updated Hook
                await _hookManager.TriggerAsync(
                    HookPoints.SessionUpdated,
                    new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["changeType"] = "context_updated",
                        ["changeData"] = new { key, value }
                    });
            }
        }

        /// <summary>
        /// 获取会话上下文值
        /// </summary>
        public T? GetContext<T>(string sessionId, string key)
        {
            var session = GetSession(sessionId);
            if (session == null) return default;
            return session.GetContextValue<T>(key);
        }

        /// <summary>
        /// 获取所有活跃会话
        /// </summary>
        public IReadOnlyCollection<SessionData> GetActiveSessions() => _sessions.Values.ToList().AsReadOnly();

        /// <summary>
        /// 清理过期会话
        /// </summary>
        public async Task CleanupExpiredSessionsAsync(TimeSpan expiration)
        {
            var threshold = DateTime.UtcNow - expiration;
            var expiredSessions = _sessions.Where(s => s.Value.LastActiveAt < threshold).Select(s => s.Key).ToList();

            foreach (var sessionId in expiredSessions)
            {
                await DeleteSessionAsync(sessionId);
            }

            if (expiredSessions.Count > 0)
            {
                _logger.LogInformation("清理过期会话: {Count} 个", expiredSessions.Count);
            }
        }

        /// <summary>
        /// 清理过期会话（同步版本，向后兼容）
        /// </summary>
        public void CleanupExpiredSessions(TimeSpan expiration)
        {
            CleanupExpiredSessionsAsync(expiration).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 设置会话为空闲状态（触发 session.idle Hook）
        /// </summary>
        public async Task SetIdleAsync(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                _logger.LogWarning("会话不存在，无法设置空闲状态: {SessionId}", sessionId);
                return;
            }

            _logger.LogDebug("会话进入空闲状态: {SessionId}", sessionId);

            // 触发 session.idle Hook
            await _hookManager.TriggerAsync(
                HookPoints.SessionIdle,
                new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["session"] = session
                });
        }

        /// <summary>
        /// 设置会话错误状态（触发 session.error Hook）
        /// </summary>
        public async Task SetErrorAsync(string sessionId, Exception error)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                _logger.LogWarning("会话不存在，无法设置错误状态: {SessionId}", sessionId);
                return;
            }

            _logger.LogError(error, "会话发生错误: {SessionId}", sessionId);

            // 触发 session.error Hook
            await _hookManager.TriggerAsync(
                HookPoints.SessionError,
                new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["session"] = session,
                    ["error"] = error
                });
        }

        private string GenerateSessionId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"ses_{timestamp}_{random}";
        }
    }
}