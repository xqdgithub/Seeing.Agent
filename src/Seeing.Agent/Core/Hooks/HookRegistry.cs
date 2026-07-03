namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 点注册表 - 定义所有 Hook 点及其执行策略
/// </summary>
public static class HookRegistry
{
    #region Agent 生命周期

    /// <summary>Agent 调用前 - 阻塞策略</summary>
    public static readonly HookSpec AgentBeforeInvoke = new(HookPolicy.Blocking, "agent.before_invoke");

    /// <summary>Agent 调用后 - 非阻塞策略</summary>
    public static readonly HookSpec AgentAfterInvoke = new(HookPolicy.FireAndForget, "agent.after_invoke");

    /// <summary>子 Agent 启动 - 非阻塞策略</summary>
    public static readonly HookSpec SubagentStarted = new(HookPolicy.FireAndForget, "agent.subagent.started");

    /// <summary>子 Agent 完成 - 非阻塞策略</summary>
    public static readonly HookSpec SubagentCompleted = new(HookPolicy.FireAndForget, "agent.subagent.completed");

    #endregion

    #region Chat 生命周期

    /// <summary>Chat 开始前 - 阻塞策略</summary>
    public static readonly HookSpec ChatBeforeStart = new(HookPolicy.Blocking, "chat.before_start");

    /// <summary>Chat 参数处理 - 阻塞策略</summary>
    public static readonly HookSpec ChatParams = new(HookPolicy.Blocking, "chat.params");

    /// <summary>Chat 请求头处理 - 阻塞策略</summary>
    public static readonly HookSpec ChatHeaders = new(HookPolicy.Blocking, "chat.headers");

    /// <summary>Chat 完成后 - 非阻塞策略</summary>
    public static readonly HookSpec ChatAfterComplete = new(HookPolicy.FireAndForget, "chat.after_complete");

    /// <summary>Chat 消息处理 - 并行策略</summary>
    public static readonly HookSpec ChatMessage = new(HookPolicy.Parallel, "chat.message");

    /// <summary>Chat 错误处理 - 非阻塞策略</summary>
    public static readonly HookSpec ChatOnError = new(HookPolicy.FireAndForget, "chat.on_error");

    #endregion

    #region Tool 生命周期

    /// <summary>Tool 注册前 - 阻塞策略</summary>
    public static readonly HookSpec ToolBeforeRegister = new(HookPolicy.Blocking, "tool.before_register");

    /// <summary>Tool 定义处理 - 阻塞策略</summary>
    public static readonly HookSpec ToolDefinition = new(HookPolicy.Blocking, "tool.definition");

    /// <summary>Tool 执行前 - 阻塞策略</summary>
    public static readonly HookSpec ToolExecuteBefore = new(HookPolicy.Blocking, "tool.execute.before");

    /// <summary>Tool 注册后 - 非阻塞策略</summary>
    public static readonly HookSpec ToolAfterRegister = new(HookPolicy.FireAndForget, "tool.after_register");

    /// <summary>Tool 执行后 - 非阻塞策略</summary>
    public static readonly HookSpec ToolExecuteAfter = new(HookPolicy.FireAndForget, "tool.execute.after");

    /// <summary>Tool 执行错误 - 非阻塞策略</summary>
    public static readonly HookSpec ToolOnError = new(HookPolicy.FireAndForget, "tool.on_error");

    /// <summary>Tool 批量执行前 - 阻塞策略</summary>
    public static readonly HookSpec ToolBatchBefore = new(HookPolicy.Blocking, "tool.batch.before");

    /// <summary>Tool 批量执行后 - 非阻塞策略</summary>
    public static readonly HookSpec ToolBatchAfter = new(HookPolicy.FireAndForget, "tool.batch.after");

    #endregion

    #region Session 生命周期

    /// <summary>Session 创建 - 非阻塞策略</summary>
    public static readonly HookSpec SessionCreated = new(HookPolicy.FireAndForget, "session.created");

    /// <summary>Session 激活 - 非阻塞策略</summary>
    public static readonly HookSpec SessionActivated = new(HookPolicy.FireAndForget, "session.activated");

    /// <summary>Session 暂停 - 非阻塞策略</summary>
    public static readonly HookSpec SessionPaused = new(HookPolicy.FireAndForget, "session.paused");

    /// <summary>Session 恢复 - 非阻塞策略</summary>
    public static readonly HookSpec SessionResumed = new(HookPolicy.FireAndForget, "session.resumed");

    /// <summary>Session 完成 - 非阻塞策略</summary>
    public static readonly HookSpec SessionCompleted = new(HookPolicy.FireAndForget, "session.completed");

    /// <summary>Session 销毁 - 非阻塞策略</summary>
    public static readonly HookSpec SessionDestroyed = new(HookPolicy.FireAndForget, "session.destroyed");

    /// <summary>Session 保存前 - 非阻塞策略</summary>
    public static readonly HookSpec SessionSaving = new(HookPolicy.FireAndForget, "session.saving");

    /// <summary>Session 保存后 - 非阻塞策略</summary>
    public static readonly HookSpec SessionSaved = new(HookPolicy.FireAndForget, "session.saved");

    /// <summary>Session 加载前 - 非阻塞策略</summary>
    public static readonly HookSpec SessionLoading = new(HookPolicy.FireAndForget, "session.loading");

    /// <summary>Session 加载后 - 非阻塞策略</summary>
    public static readonly HookSpec SessionLoaded = new(HookPolicy.FireAndForget, "session.loaded");

    /// <summary>Session 压缩后 - 非阻塞策略</summary>
    public static readonly HookSpec SessionCompressed = new(HookPolicy.FireAndForget, "session.compressed");

    #endregion

    #region Memory 生命周期

    /// <summary>Memory 存储前 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryBeforeStore = new(HookPolicy.FireAndForget, "memory.before_store");

    /// <summary>Memory 存储后 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryAfterStore = new(HookPolicy.FireAndForget, "memory.after_store");

    /// <summary>Memory 检索前 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryBeforeRetrieve = new(HookPolicy.FireAndForget, "memory.before_retrieve");

    /// <summary>Memory 检索后 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryAfterRetrieve = new(HookPolicy.FireAndForget, "memory.after_retrieve");

    /// <summary>Memory 清除前 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryBeforeClear = new(HookPolicy.FireAndForget, "memory.before_clear");

    /// <summary>Memory 清除后 - 非阻塞策略</summary>
    public static readonly HookSpec MemoryAfterClear = new(HookPolicy.FireAndForget, "memory.after_clear");

    #endregion

    #region 权限与检测

    /// <summary>权限请求 - 阻塞策略</summary>
    public static readonly HookSpec PermissionAsk = new(HookPolicy.Blocking, "permission.ask");

    /// <summary>权限拒绝 - 非阻塞策略</summary>
    public static readonly HookSpec PermissionDenied = new(HookPolicy.FireAndForget, "permission.denied");

    /// <summary>循环检测 - 非阻塞策略</summary>
    public static readonly HookSpec LoopDetected = new(HookPolicy.FireAndForget, "permission.loop.detected");

    #endregion

    #region LLM 调用

    /// <summary>LLM 系统提示词处理 - 阻塞策略</summary>
    public static readonly HookSpec LlmSystemPrompt = new(HookPolicy.Blocking, "llm.system_prompt");

    #endregion

    #region Shell/命令

    /// <summary>Shell 环境变量处理 - 阻塞策略</summary>
    public static readonly HookSpec ShellEnv = new(HookPolicy.Blocking, "shell.env");

    /// <summary>Shell 命令执行前 - 阻塞策略</summary>
    public static readonly HookSpec ShellCommandExecuteBefore = new(HookPolicy.Blocking, "shell.command.execute.before");

    /// <summary>命令执行前 - 阻塞策略</summary>
    public static readonly HookSpec CommandExecuteBefore = new(HookPolicy.Blocking, "command.execute.before");

    #endregion

    #region MCP 生命周期

    /// <summary>MCP 初始化前 - 阻塞策略</summary>
    public static readonly HookSpec McpBeforeInitialize = new(HookPolicy.Blocking, "mcp.before_initialize");

    /// <summary>MCP Server 连接前 - 阻塞策略（可拦截）</summary>
    public static readonly HookSpec McpBeforeConnect = new(HookPolicy.Blocking, "mcp.before_connect");

    /// <summary>MCP Server 连接后 - 非阻塞策略</summary>
    public static readonly HookSpec McpAfterConnect = new(HookPolicy.FireAndForget, "mcp.after_connect");

    /// <summary>MCP Server 断开连接 - 非阻塞策略</summary>
    public static readonly HookSpec McpDisconnected = new(HookPolicy.FireAndForget, "mcp.disconnected");

    /// <summary>MCP Server 状态变更 - 非阻塞策略</summary>
    public static readonly HookSpec McpStatusChanged = new(HookPolicy.FireAndForget, "mcp.status_changed");

    /// <summary>MCP Server 错误 - 非阻塞策略</summary>
    public static readonly HookSpec McpOnError = new(HookPolicy.FireAndForget, "mcp.on_error");

    /// <summary>MCP 工具注册前 - 阻塞策略</summary>
    public static readonly HookSpec McpToolBeforeRegister = new(HookPolicy.Blocking, "mcp.tool.before_register");

    /// <summary>MCP 工具注册后 - 非阻塞策略</summary>
    public static readonly HookSpec McpToolAfterRegister = new(HookPolicy.FireAndForget, "mcp.tool.after_register");

    /// <summary>MCP 工具注销 - 非阻塞策略</summary>
    public static readonly HookSpec McpToolUnregistered = new(HookPolicy.FireAndForget, "mcp.tool.unregistered");

    /// <summary>MCP 重连前 - 阻塞策略</summary>
    public static readonly HookSpec McpBeforeReconnect = new(HookPolicy.Blocking, "mcp.before_reconnect");

    /// <summary>MCP 重连后 - 非阻塞策略</summary>
    public static readonly HookSpec McpAfterReconnect = new(HookPolicy.FireAndForget, "mcp.after_reconnect");

    /// <summary>MCP 配置更新前 - 阻塞策略</summary>
    public static readonly HookSpec McpBeforeConfigUpdate = new(HookPolicy.Blocking, "mcp.before_config_update");

    /// <summary>MCP 关闭 - 非阻塞策略</summary>
    public static readonly HookSpec McpShutdown = new(HookPolicy.FireAndForget, "mcp.shutdown");

    #endregion

    #region Scheduler 生命周期

    /// <summary>调度任务执行前 - 阻塞策略</summary>
    public static readonly HookSpec SchedulerJobBeforeExecute = new(HookPolicy.Blocking, "scheduler.job.before_execute");

    /// <summary>调度任务执行后 - 非阻塞策略</summary>
    public static readonly HookSpec SchedulerJobAfterExecute = new(HookPolicy.FireAndForget, "scheduler.job.after_execute");

    /// <summary>心跳执行前 - 阻塞策略</summary>
    public static readonly HookSpec SchedulerHeartbeatBefore = new(HookPolicy.Blocking, "scheduler.heartbeat.before");

    /// <summary>心跳执行后 - 非阻塞策略</summary>
    public static readonly HookSpec SchedulerHeartbeatAfter = new(HookPolicy.FireAndForget, "scheduler.heartbeat.after");

    #endregion
}