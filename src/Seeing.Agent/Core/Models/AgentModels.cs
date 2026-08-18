using Seeing.Agent.Abstractions.Agents;
namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Agent 执行结果
    /// </summary>
    public class AgentResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>生成的消息列表</summary>
        public List<Seeing.Agent.Abstractions.Llm.ChatMessage> Messages { get; set; } = new();

        /// <summary>输出文本</summary>
        public string Output { get; set; } = string.Empty;

        /// <summary>错误信息</summary>
        public Exception? Error { get; set; }
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

    /// <summary>AgentContext.Metadata 键名（Gateway / ACP 透传共享）</summary>
    public static class AgentContextKeys
    {
        /// <summary>请求级 Model ID（用户在界面选择的模型，优先级最高）</summary>
        /// <remarks>
        /// 适用于 Native Agent 和 ACP Passthrough。
        /// 优先级：RequestModelId &gt; Agent.Model &gt; DefaultModel
        /// </remarks>
        public const string RequestModelId = "request:modelId";

        /// <summary>ACP 透传 session mode（如 build / ask）</summary>
        public const string AcpModeId = "acp:modeId";
    }
}