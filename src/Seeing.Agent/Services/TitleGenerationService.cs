using Microsoft.Extensions.Logging;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Services
{
    /// <summary>
    /// 会话标题生成服务 - 使用 title Agent 生成标题
    /// </summary>
    /// <remarks>
    /// 通过 Hook 机制监听会话更新，自动生成标题，完全解耦：
    /// - 不依赖 SessionManager 构造函数注入
    /// - 实现 IHookHandler 监听 session.updated 事件
    /// - 自动检测是否需要生成标题
    /// </remarks>
    public class TitleGenerationService : ITitleGenerationService, IHookHandler
    {
        private readonly AgentExecutor _agentExecutor;
        private readonly IAgentRegistry _agentRegistry;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<TitleGenerationService>? _logger;

        // 默认标题前缀（用于判断是否需要生成）
        private const string DefaultTitlePrefix = "Session ";

        /// <summary>
        /// Hook 规格 - 监听 session.updated
        /// </summary>
        public HookSpec Spec => new HookSpec(HookPolicy.FireAndForget, "session.updated");

        /// <summary>
        /// Hook 优先级
        /// </summary>
        public int Priority => 100;

        /// <summary>
        /// 创建标题生成服务
        /// </summary>
        public TitleGenerationService(
            AgentExecutor agentExecutor,
            IAgentRegistry agentRegistry,
            ISessionManager sessionManager,
            ILogger<TitleGenerationService>? logger = null)
        {
            _agentExecutor = agentExecutor;
            _agentRegistry = agentRegistry;
            _sessionManager = sessionManager;
            _logger = logger;
        }

        /// <summary>
        /// 处理 Hook 事件
        /// </summary>
        public Task<HookResult> ExecuteAsync(HookPayload payload)
        {
            var data = payload.Result ?? payload.Input;
            if (data == null) return Task.FromResult(HookResult.Success);

            // 获取消息对象
            if (!data.TryGetValue("message", out var msgObj) || msgObj is not SessionMessage message)
                return Task.FromResult(HookResult.Success);

            // 只处理用户消息
            if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(HookResult.Success);

            // 忽略 synthetic 消息
            if (message.Metadata.ContainsKey("synthetic"))
                return Task.FromResult(HookResult.Success);

            var sessionId = payload.SessionId;

            // 获取会话
            var session = _sessionManager.Get(sessionId);
            if (session == null) return Task.FromResult(HookResult.Success);

            // 计算真实用户消息数量
            var realUserMessageCount = session.Messages.Count(m =>
                m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                !m.Metadata.ContainsKey("synthetic"));

            // 检查是否应该生成标题
            if (!ShouldGenerateTitle(session.Title, session.ParentSessionId, realUserMessageCount))
                return Task.FromResult(HookResult.Success);

            // 异步生成标题（不阻塞主流程）
            var userContent = message.Content ?? string.Empty;
            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await GenerateTitleAsync(sessionId, userContent);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        await _sessionManager.SetTitleAsync(sessionId, title);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "标题生成失败: SessionId={SessionId}", sessionId);
                }
            });

            return Task.FromResult(HookResult.Success);
        }

        /// <summary>
        /// 检查是否应该为会话生成标题
        /// </summary>
        public bool ShouldGenerateTitle(
            string sessionTitle,
            string? parentSessionId,
            int realUserMessageCount)
        {
            // 如果是 Fork 的会话，不生成标题
            if (!string.IsNullOrEmpty(parentSessionId))
                return false;

            // 如果标题已经不是默认标题，不生成
            if (!IsDefaultTitle(sessionTitle))
                return false;

            // 只在第一条真实用户消息后生成
            return realUserMessageCount == 1;
        }

        /// <summary>
        /// 判断是否是默认标题
        /// </summary>
        private static bool IsDefaultTitle(string title)
        {
            return title.StartsWith(DefaultTitlePrefix, StringComparison.OrdinalIgnoreCase) ||
                   title.StartsWith("New Session", StringComparison.OrdinalIgnoreCase) ||
                   string.IsNullOrWhiteSpace(title);
        }

        /// <summary>
        /// 为会话生成标题
        /// </summary>
        public async Task<string?> GenerateTitleAsync(
            string sessionId,
            string userMessage,
            CancellationToken ct = default)
        {
            try
            {
                // 获取 title agent 配置
                var titleAgentInfo = await _agentRegistry.GetAgentAsync("title");
                if (titleAgentInfo == null)
                {
                    _logger?.LogWarning("未找到 title agent，无法生成标题");
                    return null;
                }

                // 转换为 AgentDefinition
                var titleAgent = new AgentDefinition
                {
                    Name = titleAgentInfo.Name,
                    SystemPrompt = titleAgentInfo.SystemPrompt,
                    Temperature = titleAgentInfo.Temperature ?? 0.5,
                    MaxSteps = 1, // 标题生成只需一步
                };

                // 创建执行上下文
                var context = new AgentContext
                {
                    SessionId = sessionId,
                    MessageId = Guid.NewGuid().ToString("N"),
                    CancellationToken = ct,
                    History = new List<ChatMessage>
                    {
                        new() { Role = ChatRole.User, Content = TruncateMessage(userMessage) }
                    },
                    // 使用默认权限通道（title agent 已禁用所有工具）
                    PermissionChannel = DefaultPermissionChannel.AutoApproveInstance,
                };

                // 执行 title agent
                var titleContent = new System.Text.StringBuilder();
                await foreach (var evt in _agentExecutor.ExecuteAsync(titleAgent, context, ct))
                {
                    if (evt is StreamDeltaEvent delta)
                    {
                        titleContent.Append(delta.ContentDelta);
                    }
                    else if (evt is ErrorEvent error)
                    {
                        _logger?.LogWarning("Title agent 执行出错: {Message}", error.Message);
                        return null;
                    }
                }

                var rawTitle = titleContent.ToString();
                if (string.IsNullOrWhiteSpace(rawTitle))
                {
                    _logger?.LogWarning("Title agent 返回空标题: SessionId={SessionId}", sessionId);
                    return null;
                }

                // 清理标题
                var cleanedTitle = CleanTitle(rawTitle);

                if (string.IsNullOrWhiteSpace(cleanedTitle))
                {
                    return null;
                }

                _logger?.LogInformation("生成会话标题: SessionId={SessionId}, Title={Title}",
                    sessionId, cleanedTitle);

                return cleanedTitle;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("标题生成被取消: SessionId={SessionId}", sessionId);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "生成标题失败: SessionId={SessionId}", sessionId);
                return null;
            }
        }

        /// <summary>
        /// 清理标题文本
        /// </summary>
        public string CleanTitle(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return string.Empty;

            // 移除思考标签（某些模型可能会输出）
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                rawTitle,
                @"<tool_call>think.*?<\/think>\s*",
                "",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            // 按行分割，取第一个非空行
            var lines = cleaned.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (lines.Count == 0)
                return string.Empty;

            cleaned = lines[0];

            // 限制长度
            if (cleaned.Length > 50)
            {
                cleaned = cleaned.Substring(0, 47) + "...";
            }

            return cleaned;
        }

        /// <summary>
        /// 截断消息（避免输入过长）
        /// </summary>
        private static string TruncateMessage(string message, int maxLength = 1000)
        {
            if (string.IsNullOrEmpty(message))
                return string.Empty;

            if (message.Length <= maxLength)
                return message;

            return message.Substring(0, maxLength) + "...";
        }
    }
}
