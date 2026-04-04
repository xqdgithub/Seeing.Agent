using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hooks;

namespace Seeing.Agent.Core.Abstractions
{
    /// <summary>
    /// Agent 基类 - 提供常用 Agent 实现的便捷方法
    /// </summary>
    public abstract class AgentBase : IAgent
    {
        protected readonly ILogger _logger;
        protected readonly IHookManager? _hookManager;

        /// <summary>
        /// 创建 Agent 基类实例（无 Hook 支持）
        /// </summary>
        protected AgentBase(ILogger logger)
        {
            _logger = logger;
            _hookManager = null;
        }

        /// <summary>
        /// 创建 Agent 基类实例（带 Hook 支持）
        /// </summary>
        protected AgentBase(ILogger logger, IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        /// <summary>Agent 名称</summary>
        public abstract string Name { get; }

        /// <summary>Agent 模式</summary>
        public virtual AgentMode Mode => AgentMode.All;

        /// <summary>Agent 描述</summary>
        public abstract string Description { get; }

        /// <summary>权限规则集</summary>
        public virtual IReadOnlyList<PermissionRule> Permissions => Array.Empty<PermissionRule>();

        /// <summary>系统提示词</summary>
        public virtual string? SystemPrompt => null;

        /// <summary>模型配置</summary>
        public virtual ModelReference? Model => null;

        /// <summary>最大迭代步骤</summary>
        public virtual int? MaxSteps => null;

        /// <summary>
        /// 执行 Agent（带 Hook 包装）
        /// 子类应实现 ExecuteCoreAsync 而不是直接重写此方法
        /// </summary>
        public async IAsyncEnumerable<ChatMessage> ExecuteAsync(
            ChatMessage input,
            AgentContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sessionId = context.SessionId;
            var inputPreview = input.Content ?? "";

            // ========== Hook: agent.before_invoke ==========
            if (_hookManager != null)
            {
                var beforeOutput = new Dictionary<string, object>
                {
                    ["agentName"] = Name,
                    ["mode"] = Mode.ToString()
                };

                var beforeResult = await _hookManager.TriggerAsync(
                    HookPoints.AgentBeforeInvoke,
                    new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["agentName"] = Name,
                        ["inputPreview"] = Truncate(inputPreview, 100)
                    },
                    beforeOutput,
                    cancellationToken);

                if (!beforeResult.Continue)
                {
                    _logger.LogWarning("Agent [{Name}] 被 Hook 中断, SessionId: {SessionId}", Name, sessionId);
                    yield return new ChatMessage { Role = "system", Content = "Agent 执行被 Hook 中断" };
                    yield break;
                }
            }

            LogStart(sessionId, inputPreview);

            // 使用 List 收集结果以避免 yield 在 try-catch 中的限制
            var results = new List<ChatMessage>();
            var success = true;
            Exception? caughtException = null;

            try
            {
                await foreach (var message in ExecuteCoreAsync(input, context, cancellationToken))
                {
                    results.Add(message);
                }
            }
            catch (Exception ex)
            {
                success = false;
                caughtException = ex;
                LogError(sessionId, ex);
                results.Add(new ChatMessage { Role = "error", Content = ex.Message });
            }

            // 返回所有结果
            foreach (var result in results)
            {
                yield return result;
            }

            LogComplete(sessionId, success);

            // ========== Hook: agent.after_invoke ==========
            if (_hookManager != null)
            {
                await _hookManager.TriggerAsync(
                    HookPoints.AgentAfterInvoke,
                    new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["agentName"] = Name,
                        ["success"] = success,
                        ["error"] = caughtException as object
                    },
                    cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// 执行 Agent 核心 logic（子类实现）
        /// </summary>
        protected abstract IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input,
            AgentContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 记录 Agent 开始执行
        /// </summary>
        protected virtual void LogStart(string sessionId, string inputPreview)
        {
            _logger.LogInformation("Agent [{Name}] 开始执行, SessionId: {SessionId}, Input: {Input}",
                Name, sessionId, Truncate(inputPreview, 100));
        }

        /// <summary>
        /// 记录 Agent 完成执行
        /// </summary>
        protected virtual void LogComplete(string sessionId, bool success)
        {
            _logger.LogInformation("Agent [{Name}] 执行完成, SessionId: {SessionId}, Success: {Success}",
                Name, sessionId, success);
        }

        /// <summary>
        /// 记录 Agent 错误
        /// </summary>
        protected virtual void LogError(string sessionId, Exception ex)
        {
            _logger.LogError(ex, "Agent [{Name}] 执行失败, SessionId: {SessionId}",
                Name, sessionId);
        }

        /// <summary>
        /// 截断字符串用于日志显示
        /// </summary>
        protected static string Truncate(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length > maxLength ? text.Substring(0, maxLength) + "..." : text;
        }
    }
}