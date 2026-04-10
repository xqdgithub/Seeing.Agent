using Seeing.Agent.Llm;
using Seeing.Agent.Sessions;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 会话管理器接口
    /// </summary>
    public interface ISessionManager
    {
        /// <summary>创建新会话（异步）</summary>
        Task<SessionData> CreateSessionAsync(IAgent? agent = null, string? cwd = null);
        
        /// <summary>创建新会话（同步，向后兼容）</summary>
        SessionData CreateSession(IAgent? agent = null);
        
        /// <summary>获取会话</summary>
        SessionData? GetSession(string sessionId);
        
        /// <summary>获取或创建会话</summary>
        SessionData GetOrCreateSession(string? sessionId, IAgent? agent = null);
        
        /// <summary>删除会话（异步）</summary>
        Task<bool> DeleteSessionAsync(string sessionId);
        
        /// <summary>删除会话（同步，向后兼容）</summary>
        bool DeleteSession(string sessionId);
        
        /// <summary>添加消息到会话（异步，带 Hook）</summary>
        Task AddMessageAsync(string sessionId, ChatMessage message);
        
        /// <summary>添加消息到会话（同步，向后兼容）</summary>
        void AddMessage(string sessionId, ChatMessage message);
        
        /// <summary>获取会话消息历史</summary>
        IReadOnlyList<ChatMessage> GetMessages(string sessionId);
        
        /// <summary>设置会话上下文值（异步，带 Hook）</summary>
        Task SetContextAsync(string sessionId, string key, object value);
        
        /// <summary>设置会话上下文值（同步，向后兼容）</summary>
        void SetContext(string sessionId, string key, object value);
        
        /// <summary>获取会话上下文值</summary>
        T? GetContext<T>(string sessionId, string key);
        
        /// <summary>获取所有活跃会话</summary>
        IReadOnlyCollection<SessionData> GetActiveSessions();
        
        /// <summary>清理过期会话（异步）</summary>
        Task CleanupExpiredSessionsAsync(TimeSpan expiration);
        
        /// <summary>清理过期会话（同步，向后兼容）</summary>
        void CleanupExpiredSessions(TimeSpan expiration);
        
        /// <summary>设置会话为空闲状态（触发 session.idle Hook）</summary>
        Task SetIdleAsync(string sessionId);
        
        /// <summary>设置会话错误状态（触发 session.error Hook）</summary>
        Task SetErrorAsync(string sessionId, Exception error);
        
        /// <summary>
        /// 压缩会话历史（Sliding Window）
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>压缩后保留的消息数量</returns>
        Task<int> CompactAsync(string sessionId, CancellationToken cancellationToken = default);
    }
}