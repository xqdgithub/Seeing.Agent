using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 回滚器
    /// </summary>
    public class SessionReverter
    {
        private readonly ILogger<SessionReverter> _logger;
        private readonly ISessionManager _sessionManager;

        public SessionReverter(ILogger<SessionReverter> logger, ISessionManager sessionManager)
        {
            _logger = logger;
            _sessionManager = sessionManager;
        }

        /// <summary>Revert Session - 回滚到指定消息</summary>
        public async Task<bool> RevertAsync(
            string sessionId,
            string messageId,
            CancellationToken ct = default)
        {
            var session = _sessionManager.Get(sessionId);
            if (session == null)
            {
                _logger.LogWarning("Session not found: {SessionId}", sessionId);
                return false;
            }

            var messageIndex = session.Messages.FindIndex(m => m.Id == messageId);
            if (messageIndex < 0)
            {
                _logger.LogWarning("Message not found: {MessageId} in session {SessionId}", messageId, sessionId);
                return false;
            }

            // 保留到该消息（含）
            var messagesToKeep = messageIndex + 1;
            var removedCount = session.Messages.Count - messagesToKeep;

            session.Messages = session.Messages.Take(messagesToKeep).ToList();
            session.UpdatedAt = DateTime.Now;
            session.LastActiveAt = DateTime.Now;

            // 保存
            await _sessionManager.SaveAsync(sessionId);

            _logger.LogInformation("Reverted session {SessionId} to message {MessageId}, removed {Count} messages",
                sessionId, messageId, removedCount);

            return true;
        }

        /// <summary>回滚到最后一条用户消息</summary>
        public async Task<bool> RevertToLastUserMessageAsync(string sessionId, CancellationToken ct = default)
        {
            var session = _sessionManager.Get(sessionId);
            if (session == null) return false;

            var lastUserMessage = session.Messages.LastOrDefault(m => m.Role == "user");
            if (lastUserMessage == null) return false;

            return await RevertAsync(sessionId, lastUserMessage.Id, ct);
        }

        /// <summary>回滚指定数量的消息</summary>
        public async Task<int> RevertMessagesAsync(string sessionId, int count, CancellationToken ct = default)
        {
            var session = _sessionManager.Get(sessionId);
            if (session == null) return 0;

            var actualCount = Math.Min(count, session.Messages.Count);
            if (actualCount == 0) return 0;

            session.Messages = session.Messages.Take(session.Messages.Count - actualCount).ToList();
            session.UpdatedAt = DateTime.Now;

            await _sessionManager.SaveAsync(sessionId);

            _logger.LogInformation("Reverted {Count} messages from session {SessionId}", actualCount, sessionId);
            return actualCount;
        }
    }
}
