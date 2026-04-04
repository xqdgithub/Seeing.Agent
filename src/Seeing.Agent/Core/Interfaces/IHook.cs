namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Hook 点定义 - 对齐 opencode 命名风格
    /// </summary>
    public static class HookPoints
    {
        // ===== 对话生命周期 =====
        
        /// <summary>对话开始前 - 在 LlmService.CompleteAsync 开始时触发</summary>
        public const string ChatBeforeStart = "chat.before_start";
        /// <summary>对话完成后 - 在 LlmService.CompleteAsync 结束时触发</summary>
        public const string ChatAfterComplete = "chat.after_complete";
        /// <summary>重试前 - 在 LLM 调用重试前触发（需配置重试策略）</summary>
        public const string ChatBeforeRetry = "chat.before_retry";
        /// <summary>对话出错 - 在 LLM 调用异常时触发</summary>
        public const string ChatOnError = "chat.on_error";
        /// <summary>收到新消息 - 在收到 LLM 响应后触发（opencode: chat.message）</summary>
        public const string ChatMessage = "chat.message";
        /// <summary>修改 LLM 参数 - 发送请求前可修改 temperature/topP/maxTokens（opencode: chat.params）</summary>
        public const string ChatParams = "chat.params";
        /// <summary>修改 LLM Headers - 发送请求前可添加自定义 Headers（opencode: chat.headers）</summary>
        public const string ChatHeaders = "chat.headers";
        
        // ===== 工具生命周期 =====
        
        /// <summary>工具注册前 - 在 ToolInvoker.RegisterTool 前触发（可拦截危险工具）</summary>
        public const string ToolBeforeRegister = "tool.before_register";
        /// <summary>工具注册后 - 在 ToolInvoker.RegisterTool 后触发</summary>
        public const string ToolAfterRegister = "tool.after_register";
        /// <summary>工具定义时 - 获取 Schema 时触发，可修改描述和参数（opencode: tool.definition）</summary>
        public const string ToolDefinition = "tool.definition";
        /// <summary>工具执行前 - 执行前拦截或修改参数（opencode: tool.execute.before）</summary>
        public const string ToolExecuteBefore = "tool.execute.before";
        /// <summary>工具执行后 - 执行后修改输出结果（opencode: tool.execute.after）</summary>
        public const string ToolExecuteAfter = "tool.execute.after";
        /// <summary>工具出错 - 工具执行异常时触发</summary>
        public const string ToolOnError = "tool.on_error";
        
        // ===== Session 生命周期 =====
        
        /// <summary>Session 创建 - SessionManager.CreateSessionAsync 时触发</summary>
        public const string SessionCreated = "session.created";
        /// <summary>Session 更新 - 添加消息或更新上下文时触发</summary>
        public const string SessionUpdated = "session.updated";
        /// <summary>Session 删除 - SessionManager.DeleteSessionAsync 时触发</summary>
        public const string SessionDeleted = "session.deleted";
        /// <summary>Session 压缩前 - 历史消息压缩前触发（opencode: experimental.session.compacting）</summary>
        public const string SessionCompacting = "session.compacting";
        /// <summary>Session 空闲 - Session 进入空闲状态时触发（opencode: session.idle）</summary>
        public const string SessionIdle = "session.idle";
        /// <summary>Session 错误 - Session 发生错误时触发（opencode: session.error）</summary>
        public const string SessionError = "session.error";
        
        // ===== Agent 生命周期 =====
        
        /// <summary>Agent 调用前 - AgentBase.ExecuteAsync 开始时触发</summary>
        public const string AgentBeforeInvoke = "agent.before_invoke";
        /// <summary>Agent 调用后 - AgentBase.ExecuteAsync 结束时触发</summary>
        public const string AgentAfterInvoke = "agent.after_invoke";
        
        // ===== 权限 =====
        
        /// <summary>权限询问 - RuleEngine 评估 Ask 动作时触发，Hook 可覆盖决策（opencode: permission.ask）</summary>
        public const string PermissionAsk = "permission.ask";
        
        // ===== LLM 调用 =====
        
        /// <summary>系统提示词修改 - 发送请求前可修改系统提示词（opencode: experimental.chat.system.transform）</summary>
        public const string LlmSystemPrompt = "llm.system_prompt";
        
        // ===== Shell/命令执行 =====
        
        /// <summary>Shell 环境变量 - 执行 shell 命令前触发，可注入环境变量（opencode: shell.env）</summary>
        public const string ShellEnv = "shell.env";
        /// <summary>命令执行前 - 执行自定义命令前触发，可修改参数（opencode: command.execute.before）</summary>
        public const string CommandExecuteBefore = "command.execute.before";
    }

    /// <summary>
    /// Hook 上下文
    /// </summary>
    public class HookContext
    {
        /// <summary>Hook 点</summary>
        public string HookPoint { get; set; } = string.Empty;
        
        /// <summary>输入数据</summary>
        public Dictionary<string, object> Data { get; set; } = new();
        
        /// <summary>输出数据（可被 Hook 修改）</summary>
        public Dictionary<string, object> Output { get; set; } = new();
        
        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Hook 结果
    /// </summary>
    public class HookResult
    {
        /// <summary>是否继续执行后续 Hook</summary>
        public bool Continue { get; set; } = true;
        
        /// <summary>修改后的数据</summary>
        public object? ModifiedData { get; set; }
        
        /// <summary>错误信息</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Hook 处理器接口
    /// </summary>
    public interface IHookHandler
    {
        /// <summary>Hook 点</summary>
        string HookPoint { get; }
        
        /// <summary>优先级 (越小越先执行)</summary>
        int Priority { get; }
        
        /// <summary>执行 Hook</summary>
        Task<HookResult> ExecuteAsync(HookContext context);
    }
}