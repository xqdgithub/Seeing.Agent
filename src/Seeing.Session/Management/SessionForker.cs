using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 分支器
    /// </summary>
    public class SessionForker
    {
        private readonly ILogger<SessionForker> _logger;
        private readonly SessionManager _sessionManager;

        /// <summary>
        /// 创建 SessionForker 实例
        /// </summary>
        /// <param name="logger">日志器</param>
        /// <param name="sessionManager">Session 管理器</param>
        public SessionForker(ILogger<SessionForker> logger, SessionManager sessionManager)
        {
            _logger = logger;
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

            var forkedSession = SessionData.Create(sourceSession.PartitionId, sourceSession.SelectedAgent);

            // SubAgent 分叉：产出可操作的独立 Root（无 Parent）；其它会话保持 Fork 谱系
            if (sourceSession.Kind == SessionKind.SubAgent)
            {
                forkedSession.Kind = SessionKind.Root;
                forkedSession.ParentSessionId = null;
                forkedSession.ForkLabel = label ?? $"Detached from {sessionId}";
                forkedSession.Title = label ?? $"{sourceSession.Title} (独立会话)";
            }
            else
            {
                forkedSession.Kind = SessionKind.Fork;
                forkedSession.ParentSessionId = sessionId;
                forkedSession.ForkLabel = label ?? $"Fork of {sessionId}";
                forkedSession.Title = $"{sourceSession.Title} (Fork)";
            }

            forkedSession.WorkingDirectory = sourceSession.WorkingDirectory;
            forkedSession.SelectedModel = sourceSession.SelectedModel;
            forkedSession.SelectedModelProvider = sourceSession.SelectedModelProvider;

            if (atMessageId != null)
            {
                var messageIndex = sourceSession.Messages.FindIndex(m => m.Id == atMessageId);
                if (messageIndex >= 0)
                {
                    for (int i = 0; i < messageIndex; i++)
                    {
                        forkedSession.Messages.Add(CloneMessage(sourceSession.Messages[i]));
                    }
                }
            }
            else
            {
                foreach (var msg in sourceSession.Messages)
                {
                    forkedSession.Messages.Add(CloneMessage(msg));
                }
            }

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
