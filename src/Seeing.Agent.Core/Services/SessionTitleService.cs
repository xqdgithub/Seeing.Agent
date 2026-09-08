using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using System.Text.RegularExpressions;
using Seeing.Session.Core;

namespace Seeing.Agent.Services
{
    /// <summary>
    /// 会话标题确保：判定时机、清洗 LLM 输出，并完成一次受限 LLM 调用。
    /// </summary>
    public sealed class SessionTitleService : ISessionTitleService
    {
        private const string DefaultTitlePrefix = "Session ";
        private const string SyntheticMetadataKey = "synthetic";
        private const int TitleMaxLength = 15;
        /// <summary>需覆盖 thinking 模型的 reasoning 占用，过小会导致 Content 为空。</summary>
        private const int TitleMaxTokens = 4096;

        private readonly ITextCompletion _text;
        private readonly ISessionManager _sessionManager;
        private readonly IOptionsMonitor<SeeingAgentOptions> _options;
        private readonly ILogger<SessionTitleService> _logger;

        private static readonly Regex ThinkingTagRegex = new(
            @"<think>.*?</think>|<tool_call>think.*?</(?:redacted_thinking|think)>\s*",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public SessionTitleService(
            ITextCompletion text,
            ISessionManager sessionManager,
            IOptionsMonitor<SeeingAgentOptions> options,
            ILogger<SessionTitleService> logger)
        {
            _text = text;
            _sessionManager = sessionManager;
            _options = options;
            _logger = logger;
        }

        internal static bool IsDefaultTitle(string title)
        {
            return title.StartsWith(DefaultTitlePrefix, StringComparison.OrdinalIgnoreCase) ||
                   title.StartsWith("New Session", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("新会话", StringComparison.Ordinal) ||
                   string.IsNullOrWhiteSpace(title);
        }

        /// <summary>
        /// 是否计入「意图用户消息」（排除 synthetic / 项目指令注入）。
        /// </summary>
        internal static bool IsIntentionalUserMessage(SessionMessage message)
        {
            if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                return false;

            var metadata = message.Metadata;
            if (metadata == null)
                return true;

            if (metadata.ContainsKey(SyntheticMetadataKey))
                return false;

            // AGENTS.md 等注入的 user 消息，不能占用「首条真实用户消息」名额
            if (metadata.ContainsKey(ProjectInstructions.MetadataKeys.ProjectInstructions))
                return false;

            return true;
        }

        internal static int CountIntentionalUserMessages(IEnumerable<SessionMessage> messages)
            => messages.Count(IsIntentionalUserMessage);

        internal static bool ShouldEnsure(
            bool enabled,
            SessionKind kind,
            string? parentId,
            string title,
            int realUserCount,
            string userMessage)
        {
            if (!enabled)
                return false;

            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            if (!string.IsNullOrEmpty(parentId))
                return false;

            if (kind != SessionKind.Root)
                return false;

            if (realUserCount < 10 && IsDefaultTitle(title))
            {
                return true;
            }

            if (realUserCount % 10 == 0)
                return true;

            return false;
        }

        /// <summary>
        /// 默认标题可写；每 10 条意图消息允许覆盖已有标题（后续刷新）。
        /// </summary>
        internal static bool ShouldWriteTitle(string currentTitle, int realUserCount)
            => IsDefaultTitle(currentTitle) || realUserCount % 10 == 0;

        internal static string CleanTitle(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return string.Empty;

            var cleaned = ThinkingTagRegex.Replace(rawTitle, string.Empty);

            var lines = cleaned.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (lines.Count == 0)
                return string.Empty;

            cleaned = lines[0].Trim('"', '\'');

            if (cleaned.Length > TitleMaxLength)
                cleaned = cleaned[..TitleMaxLength];

            return cleaned;
        }

        /// <inheritdoc />
        public async Task<string?> TryEnsureAsync(
            string sessionId,
            string userMessage,
            string? fallbackModelId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var options = _options.CurrentValue.TitleGeneration;
                if (!options.Enabled)
                    return null;

                var session = _sessionManager.Get(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("TitleEnsure: session missing. SessionId={SessionId}", sessionId);
                    return null;
                }

                // 标题生成基于活跃消息（已压缩的旧消息不参与标题决策与输入）
                var activeMessages = session.GetActiveMessages();
                var realUserCount = CountIntentionalUserMessages(activeMessages);

                if (!ShouldEnsure(
                        options.Enabled,
                        session.Kind,
                        session.ParentSessionId,
                        session.Title,
                        realUserCount,
                        userMessage))
                {
                    return null;
                }

                var model = options.Model ?? fallbackModelId;
                if (string.IsNullOrWhiteSpace(model))
                {
                    _logger.LogWarning(
                        "TitleEnsure: no model. SessionId={SessionId}, Fallback={Fallback}",
                        sessionId,
                        fallbackModelId);
                    return null;
                }

                var history = BuildTitleHistory(session);
                if (history.Count == 0)
                {
                    history.Add(new ChatMessage { Role = ChatRole.User, Content = userMessage });
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

                string rawTitle;
                try
                {
                    rawTitle = await _text.CompleteAsync(
                        TitlePrompts.System,
                        history,
                        model,
                        TitleMaxTokens,
                        cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "TitleEnsure LLM failed: SessionId={SessionId}, Model={Model}",
                        sessionId,
                        model);
                    return null;
                }

                var cleaned = CleanTitle(rawTitle);
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    _logger.LogWarning(
                        "TitleEnsure: empty title after clean. SessionId={SessionId}, RawLen={RawLen}",
                        sessionId,
                        rawTitle?.Length ?? 0);
                    return null;
                }

                var current = _sessionManager.Get(sessionId);
                if (current == null)
                    return null;

                if (!ShouldWriteTitle(current.Title, realUserCount))
                    return null;

                await _sessionManager.SetTitleAsync(sessionId, cleaned, cancellationToken);
                _logger.LogInformation(
                    "TitleEnsure wrote: SessionId={SessionId}, Title={Title}",
                    sessionId,
                    cleaned);
                return cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TitleEnsure failed: SessionId={SessionId}", sessionId);
                return null;
            }
        }

        /// <summary>
        /// 为标题补全构建会话上下文：保留全文语义，但去掉 tool 轨迹并合并连续同角色，避免 Provider 校验失败。
        /// </summary>
        internal static List<ChatMessage> BuildTitleHistory(SessionData session)
            => ChatMessageHistoryBuilder.BuildHistory(
                session.GetActiveMessages(),
                skipToolMessages: true,
                skipEmptyContent: true,
                mergeConsecutiveRoles: true);
    }
}
