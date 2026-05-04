using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Abstractions
{
    /// <summary>
    /// Agent 基类 - 提供两种实现模式
    /// <para>
    /// 1. 配置模式：返回 Definition 属性，框架使用 AgentExecutor 统一执行
    /// 2. 代码模式：实现 ExecuteCoreAsync 方法，自定义执行逻辑
    /// </para>
    /// </summary>
    public abstract class AgentBase : IAgent
    {
        protected readonly ILogger _logger;
        protected readonly Core.Hooks.IHookManager? _hookManager;

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
        protected AgentBase(ILogger logger, Core.Hooks.IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        // ========== 新增：配置驱动模式 ==========

        /// <summary>
        /// Agent 定义（配置模式）
        /// <para>
        /// 如果返回非 null，框架使用 AgentExecutor 统一执行。
        /// 子类可以重写此属性返回配置，无需实现 ExecuteCoreAsync。
        /// </para>
        /// </summary>
        public virtual AgentDefinition? Definition => null;

        /// <summary>
        /// 是否使用配置驱动模式
        /// </summary>
        public bool IsConfigDriven => Definition != null;

        // ========== 现有属性 ==========

        /// <summary>Agent 名称</summary>
        public virtual string Name { get; set; } = "";

        /// <summary>Agent 模式</summary>
        public virtual AgentMode Mode { get; set; } = AgentMode.All;

        /// <summary>Agent 描述</summary>
        public virtual string Description { get; set; } = "";

        /// <summary>权限规则集</summary>
        public virtual IReadOnlyList<PermissionRule> Permissions { get; set; } = Array.Empty<PermissionRule>();

        /// <summary>系统提示词</summary>
        public virtual string? SystemPrompt { get; set; }

        /// <summary>模型配置</summary>
        public virtual ModelReference? Model { get; set; }

        /// <summary>最大迭代步骤</summary>
        public virtual int? MaxSteps { get; set; }

        /// <summary>Agent 状态</summary>
        public virtual AgentStatus Status { get; set; } = AgentStatus.Ready;

        /// <summary>
        /// 允许的工具列表 - 子 Agent 可限制使用的工具
        /// <para>默认空数组表示允许所有工具</para>
        /// </summary>
        public virtual IReadOnlyList<string> AllowedTools { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 禁止的工具列表 - 子 Agent 可禁止特定工具
        /// <para>默认空数组表示不禁止任何工具</para>
        /// </summary>
        public virtual IReadOnlyList<string> DeniedTools { get; set; } = Array.Empty<string>();

        // ========== 统一执行入口 ==========

        /// <summary>
        /// 执行 Agent（支持配置模式和代码模式）
        /// </summary>
        public async IAsyncEnumerable<ChatMessage> ExecuteAsync(
            ChatMessage input,
            AgentContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sessionId = context.SessionId;
            var inputPreview = input.Content ?? "";

            // ========== Hook: agent.before_invoke (仅顶层调用) ==========
            var isTopLevel = string.IsNullOrEmpty(context.ParentSessionId);
            if (_hookManager != null && isTopLevel)
            {
                var beforeResult = await _hookManager.TriggerBlockingAsync(
                    HookRegistry.AgentBeforeInvoke,
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["agentName"] = Name,
                        ["mode"] = Mode.ToString(),
                        ["isTopLevel"] = isTopLevel
                    },
                    cancellationToken: cancellationToken);

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
                // ========== 配置模式：委托给 AgentExecutor ==========
                if (IsConfigDriven && context.Services != null)
                {
                    var executor = context.Services.GetService(typeof(AgentExecutor)) as AgentExecutor;
                    if (executor != null)
                    {
                        // 将输入消息添加到历史
                        context.History.Add(input);

                        // 订阅事件流并提取 ChatMessage
                        await foreach (var evt in executor.ExecuteAsync(
                            Definition!,
                            context,
                            cancellationToken))
                        {
                            // 从 StreamCompleteEvent 中提取 ChatMessage
                            if (evt is StreamCompleteEvent completeEvent)
                            {
                                results.Add(completeEvent.Message);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("AgentExecutor 未注册，回退到代码模式");
                        await foreach (var message in ExecuteCoreAsync(input, context, cancellationToken))
                        {
                            results.Add(message);
                        }
                    }
                }
                // ========== 代码模式：调用子类实现 ==========
                else
                {
                    await foreach (var message in ExecuteCoreAsync(input, context, cancellationToken))
                    {
                        results.Add(message);
                    }
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

            // ========== Hook: agent.after_invoke (仅顶层调用) ==========
            if (_hookManager != null && isTopLevel)
            {
                _hookManager.TriggerFireAndForget(
                    HookRegistry.AgentAfterInvoke,
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["agentName"] = Name,
                        ["success"] = success,
                        ["error"] = caughtException?.Message
                    });
            }
        }

        /// <summary>
        /// 执行 Agent 核心 logic（代码模式）
        /// <para>
        /// 配置驱动的 Agent 不需要实现此方法。
        /// 代码驱动的 Agent 必须重写此方法。
        /// </para>
        /// </summary>
        protected virtual IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            // 默认实现：抛出异常提示用户选择模式
            throw new NotImplementedException(
                $"Agent '{Name}' 必须实现以下之一：\n" +
                $"1. 返回 Definition 属性（配置模式，推荐）\n" +
                $"2. 重写 ExecuteCoreAsync 方法（代码模式）");
        }

        /// <summary>
        /// 记录 Agent 开始执行
        /// </summary>
        protected virtual void LogStart(string sessionId, string inputPreview)
        {
            _logger.LogInformation("Agent [{Name}] 开始执行, SessionId: {SessionId}, Input: {Input}, Mode: {Mode}",
                Name, sessionId, Truncate(inputPreview, 100), IsConfigDriven ? "配置驱动" : "代码驱动");
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