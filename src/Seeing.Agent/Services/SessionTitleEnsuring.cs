using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Llm;
using System.Text.RegularExpressions;
using Seeing.Session.Core;

namespace Seeing.Agent.Services
{
    /// <summary>
    /// 会话标题确保：判定时机、清洗 LLM 输出，并完成一次受限 LLM 调用。
    /// </summary>
    public sealed class SessionTitleEnsuring : ISessionTitleEnsuring
    {
        private const string DefaultTitlePrefix = "Session ";
        private const string SyntheticMetadataKey = "synthetic";
        private const int TitleMaxLength = 15;
        private const int TitleMaxTokens = 4096;

        private readonly ITextCompletion _text;
        private readonly ISessionManager _sessionManager;
        private readonly IOptionsMonitor<SeeingAgentOptions> _options;
        private readonly ILogger<SessionTitleEnsuring> _logger;

        private static readonly Regex ThinkingTagRegex = new(
            @"<think>.*?</think>|<tool_call>think.*?</(?:redacted_thinking|think)>\s*",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public SessionTitleEnsuring(
            ITextCompletion text,
            ISessionManager sessionManager,
            IOptionsMonitor<SeeingAgentOptions> options,
            ILogger<SessionTitleEnsuring> logger)
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

            if(realUserCount <10 && IsDefaultTitle(title))
            {
                return true;
            }
            if (realUserCount % 10== 0)
                return true;


            return true;
        }

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
                    return null;

                var realUserCount = CountIntentionalUserMessages(session.Messages);

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
                        "无法生成会话标题，未配置模型: SessionId={SessionId}",
                        sessionId);
                    return null;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

                string rawTitle;
                try
                {
                    rawTitle = await _text.CompleteAsync(
                        TitlePrompts.System,
                        BuildHistoryFromSession(session),
                        model,
                        TitleMaxTokens,
                        cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "会话标题补全失败: SessionId={SessionId}",
                        sessionId);
                    return null;
                }

                var cleaned = CleanTitle(rawTitle);
                if (string.IsNullOrWhiteSpace(cleaned))
                    return null;

                var current = _sessionManager.Get(sessionId);
                if (current == null || !IsDefaultTitle(current.Title))
                    return null;

                await _sessionManager.SetTitleAsync(sessionId, cleaned, cancellationToken);
                return cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "确保会话标题失败: SessionId={SessionId}",
                    sessionId);
                return null;
            }
        }

        /// <summary>
        /// Builds history from session.
        /// </summary>
        private static List<ChatMessage> BuildHistoryFromSession(SessionData session)
        {
            var history = new List<ChatMessage>();

            foreach (var msg in session.Messages)
            {
                var chatMessage = new ChatMessage
                {
                    Role = msg.Role,
                    Content = msg.Content,
                    ReasoningContent = msg.ReasoningContent
                };

                if (msg.Parts != null && msg.Parts.Count > 0)
                {
                    chatMessage.Parts = msg.Parts.Select(p => new ChatContentPart
                    {
                        Type = p.Type,
                        Text = p.Text,
                        Url = p.Url,
                        DataBase64 = p.DataBase64,
                        MimeType = p.MimeType,
                        FileName = p.FileName
                    }).ToList();
                }

                if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    chatMessage.ToolCalls = msg.ToolCalls.Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new FunctionCall
                        {
                            Name = tc.Name,
                            Arguments = tc.Arguments
                        }
                    }).ToList();
                }

                history.Add(chatMessage);
            }

            return history;
        }


    }
}
