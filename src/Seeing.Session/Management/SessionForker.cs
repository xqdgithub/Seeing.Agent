using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using Seeing.Session.Storage;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 分支器
    /// </summary>
    public class SessionForker
    {
        private readonly ILogger<SessionForker> _logger;
        private readonly ISessionStore _store;
        private readonly SessionManager _sessionManager;

        public SessionForker(ILogger<SessionForker> logger, ISessionStore store, SessionManager sessionManager)
        {
            _logger = logger;
            _store = store;
            _sessionManager = sessionManager;
        }

        /// <summary>Fork Session - 创建分支</summary>
        public async Task<SessionData> ForkAsync(
            string sessionId,
            string? atMessageId = null,
            string? label = null,
            CancellationToken ct = default)
        {
            var sourceSession = _sessionManager.Get(sessionId);
            if (sourceSession == null)
                throw new InvalidOperationException($"Session not found: {sessionId}");

            // 创建新 Session
            var forkedSession = SessionData.Create(sourceSession.PartitionId, sourceSession.SelectedAgent);
            forkedSession.ParentSessionId = sessionId;
            forkedSession.ForkLabel = label ?? $"Fork of {sessionId}";
            forkedSession.Title = $"{sourceSession.Title} (Fork)";
            forkedSession.WorkingDirectory = sourceSession.WorkingDirectory;

            // 复制消息
            if (atMessageId != null)
            {
                var messageIndex = sourceSession.Messages.FindIndex(m => m.Id == atMessageId);
                if (messageIndex >= 0)
                {
                    // 复制到该消息之前（不含）
                    for (int i = 0; i < messageIndex; i++)
                    {
                        forkedSession.Messages.Add(CloneMessage(sourceSession.Messages[i]));
                    }
                }
            }
            else
            {
                // 复制所有消息
                foreach (var msg in sourceSession.Messages)
                {
                    forkedSession.Messages.Add(CloneMessage(msg));
                }
            }

            // 注册并保存
            _sessionManager.Register(forkedSession);
            await _sessionManager.SaveAsync(forkedSession.Id);

            _logger.LogInformation("Forked session {SourceId} -> {ForkedId} at message {MessageId}",
                sessionId, forkedSession.Id, atMessageId ?? "all");

            return forkedSession;
        }

        private SessionMessage CloneMessage(SessionMessage msg)
        {
            return new SessionMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = msg.Role,
                Content = msg.Content,
                ToolName = msg.ToolName,
                CreatedAt = msg.CreatedAt
            };
        }
    }
}
