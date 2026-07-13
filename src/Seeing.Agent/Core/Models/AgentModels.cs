using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Agent 上下文 - 执行时的运行时信息
    /// <para>
    /// 支持多入口（TUI/API/CLI）和子代理调用。
    /// 通过 IServiceProvider 获取运行时服务。
    /// </para>
    /// </summary>
    public class AgentContext
    {
        /// <summary>会话 ID</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>消息 ID</summary>
        public string MessageId { get; set; } = string.Empty;

        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// 服务提供者（用于获取 AgentExecutor、ToolInvoker 等）
        /// </summary>
        public IServiceProvider? Services { get; set; }

        /// <summary>
        /// 权限请求通道（多入口抽象）
        /// </summary>
        public Interfaces.IPermissionChannel? PermissionChannel { get; set; }

        /// <summary>权限上下文</summary>
        public PermissionContext? PermissionContext { get; set; }

        /// <summary>
        /// 消息历史
        /// </summary>
        public List<ChatMessage> History { get; set; } = new();

        /// <summary>
        /// 工作目录
        /// </summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>工作区根目录</summary>
        public string? WorkspaceRoot { get; set; }

        /// <summary>
        /// 父会话 ID（子代理调用时）
        /// </summary>
        public string? ParentSessionId { get; set; }

        /// <summary>
        /// 是否为后台任务
        /// </summary>
        public bool IsBackground { get; set; }

        /// <summary>元数据</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>是否顶层 Agent（避免嵌套重复触发 Hook）</summary>
        public bool IsTopLevel { get; init; } = true;

        /// <summary>父代理名称（子代理时设置）</summary>
        public string? ParentAgentName { get; init; }

        /// <summary>累计步骤数（供 agent.after_invoke Hook 使用）</summary>
        public int TotalSteps { get; set; }

        /// <summary>累计 Token 使用（供 agent.after_invoke Hook 使用）</summary>
        public TokenUsage? TotalUsage { get; set; }

        /// <summary>
        /// 创建子代理上下文
        /// </summary>
        /// <param name="subSessionId">子会话 ID</param>
        /// <param name="targetAgent">目标代理定义</param>
        /// <param name="currentAgentName">当前代理名称（父代理）</param>
        /// <returns>新的执行上下文</returns>
        public AgentContext CreateSubAgentContext(string subSessionId, AgentDefinition targetAgent, string? currentAgentName = null)
        {
            // 计算子代理权限策略（与父策略求交集）
            var subPolicy = targetAgent.BuildPermissionPolicy();

            // 创建子权限上下文（带父引用）
            PermissionContext? subPermContext = null;
            if (PermissionContext != null)
            {
                // 使用父上下文创建子上下文，建立权限继承链
                subPermContext = PermissionContext.CreateSubAgentContext(targetAgent.Name, subPolicy);
            }
            else
            {
                // 无父上下文时，从当前上下文创建
                subPermContext = PermissionContext.FromAgentContext(this, subPolicy, targetAgent.Name);
            }

            return new AgentContext
            {
                SessionId = subSessionId,
                Services = Services,
                CancellationToken = CancellationToken,
                PermissionChannel = PermissionChannel,
                PermissionContext = subPermContext,
                History = new List<ChatMessage>(),
                WorkingDirectory = WorkingDirectory,
                WorkspaceRoot = WorkspaceRoot,
                ParentSessionId = SessionId,
                ParentAgentName = currentAgentName ?? ParentAgentName,
                IsBackground = targetAgent.IsBackground || targetAgent.Mode == AgentMode.SubAgent,
                IsTopLevel = false,
                Metadata = new Dictionary<string, object>(Metadata)
            };
        }
    }

    /// <summary>
    /// Agent 执行结果
    /// </summary>
    public class AgentResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>生成的消息列表</summary>
        public List<Llm.ChatMessage> Messages { get; set; } = new();

        /// <summary>输出文本</summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>错误信息</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// 模型引用 - 指向特定 Provider 的模型
    /// </summary>
    public class ModelReference
    {
        /// <summary>提供商 ID（如 openai、anthropic）</summary>
        public string ProviderId { get; set; } = string.Empty;

        /// <summary>模型 ID（如 gpt-4o、claude-3-5-sonnet）</summary>
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// 转换为字符串表示（provider/model 格式）
        /// </summary>
        public override string ToString()
        {
            return string.IsNullOrEmpty(ProviderId)
                ? ModelId
                : $"{ProviderId}/{ModelId}";
        }

        /// <summary>
        /// 从字符串解析模型引用
        /// </summary>
        public static ModelReference? Parse(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var parts = value.Split(new[] { ':', '/' }, 2);
            if (parts.Length >= 2)
            {
                return new ModelReference
                {
                    ProviderId = parts[0],
                    ModelId = parts[1]
                };
            }

            // 只有模型 ID，无 Provider
            return new ModelReference
            {
                ProviderId = string.Empty,
                ModelId = parts[0]
            };
        }
    }

    /// <summary>
    /// Agent 状态 - 表示 Agent 实例的就绪程度
    /// </summary>
    public enum AgentStatus
    {
        /// <summary>就绪 - 可以执行</summary>
        Ready,

        /// <summary>需要工厂 - 需要通过 AgentFactory 创建实例</summary>
        RequiresFactory,

        /// <summary>已禁用 - 被配置禁用</summary>
        Disabled,

        /// <summary>错误 - 初始化或执行出错</summary>
        Error
    }

    /// <summary>
    /// Agent 模式 - 定义 Agent 的角色
    /// </summary>
    public enum AgentMode
    {
        /// <summary>
        /// 主 Agent - 用户直接交互的代理
        /// <para>仅出现在 UI 的代理选择列表中</para>
        /// </summary>
        Primary,

        /// <summary>
        /// 子 Agent - 只能被其他 Agent 调用
        /// <para>不出现在 UI 的代理选择列表中</para>
        /// </summary>
        SubAgent,

        /// <summary>
        /// 通用 Agent - 可作为主 Agent 或子 Agent
        /// <para>出现在 UI 列表中，也可被子任务调用</para>
        /// <para>在 GetPrimaryAgentsAsync 中会被包含</para>
        /// </summary>
        All
    }

    /// <summary>AgentContext.Metadata 键名（Gateway / ACP 透传共享）</summary>
    public static class AgentContextKeys
    {
        public const string AcpModeId = "acp:modeId";
        public const string AcpModelId = "acp:modelId";

        /// <summary>Native Agent 会话级模型选择（用户在界面上选择的模型）</summary>
        public const string SessionModelId = "session:modelId";
    }
}