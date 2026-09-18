using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 分支器 - 纯消息复制引擎。
    /// <para>只负责克隆消息、生成新会话 Id、复制必要配置；<b>不设置</b> <c>Kind</c>、
    /// <c>GroupId</c> 等关系字段，关系由 <c>SessionGroupManager</c> 统一承载。</para>
    /// </summary>
    public class SessionForker
    {
        private readonly ILogger<SessionForker> _logger;
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// 创建 SessionForker 实例
        /// </summary>
        /// <param name="logger">日志器</param>
        /// <param name="sessionManager">Session 管理器</param>
        public SessionForker(ILogger<SessionForker> logger, ISessionManager sessionManager)
        {
            _logger = logger;
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// 复制会话：克隆全部消息到新会话，并复制必要配置。
        /// </summary>
        /// <param name="sessionId">源会话 ID</param>
        /// <param name="label">新会话标题（可选）；null 时沿用源会话标题</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>新的会话副本（关系字段由调用方设定）</returns>
        public async Task<SessionData> ForkAsync(
            string sessionId,
            string? label = null,
            CancellationToken ct = default)
        {
            var sourceSession = _sessionManager.Get(sessionId);
            if (sourceSession == null)
                throw new InvalidOperationException($"Session not found: {sessionId}");

            var forkedSession = SessionData.Create(
                sourceSession.PartitionId,
                sourceSession.SelectedAgent,
                sourceSession.Scenario);

            forkedSession.Title = label ?? sourceSession.Title;
            forkedSession.WorkingDirectory = sourceSession.WorkingDirectory;
            forkedSession.SelectedModel = sourceSession.SelectedModel;
            forkedSession.SelectedThinkingEffort = sourceSession.SelectedThinkingEffort;
            forkedSession.ScenarioOverride = CloneScenarioOverride(sourceSession.ScenarioOverride);
            CopyInstructionFingerprints(sourceSession, forkedSession);

            foreach (var msg in sourceSession.Messages)
            {
                var clone = CloneMessage(msg);
                clone.SessionId = forkedSession.Id;
                forkedSession.AddMessage(clone);
            }

            _sessionManager.Register(forkedSession);
            await _sessionManager.SaveAsync(forkedSession.Id);
            // 分支源数据需立即持久化：显式 flush 等待写回缓冲落盘；失败仅记 Warning，不阻断分支
            try
            {
                await _sessionManager.FlushAsync(forkedSession.Id, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "分支会话落盘失败，已跳过: SessionId={SessionId}", forkedSession.Id);
            }

            _logger.LogInformation("Copied session {SourceId} -> {ForkedId}",
                sessionId, forkedSession.Id);

            return forkedSession;
        }

        private static SessionMessage CloneMessage(SessionMessage msg)
        {
            var clone = msg.Clone();
            clone.Id = Guid.NewGuid().ToString("N");
            return clone;
        }

        private static SessionScenarioOverride? CloneScenarioOverride(SessionScenarioOverride? source)
        {
            if (source is null)
                return null;

            return new SessionScenarioOverride
            {
                Modules = source.Modules is null
                    ? null
                    : new SessionModulesOverride
                    {
                        Enabled = source.Modules.Enabled is null
                            ? null
                            : new List<string>(source.Modules.Enabled)
                    },
                Tools = source.Tools is null
                    ? null
                    : new SessionToolsOverride
                    {
                        Disabled = new List<string>(source.Tools.Disabled)
                    }
            };
        }

        private static void CopyInstructionFingerprints(SessionData source, SessionData target)
        {
            if (source.Metadata.TryGetValue(SessionMetadataKeys.InstructionFingerprints, out var fingerprints)
                && !string.IsNullOrEmpty(fingerprints))
            {
                target.Metadata[SessionMetadataKeys.InstructionFingerprints] = fingerprints;
            }
        }
    }
}
