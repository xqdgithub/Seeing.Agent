using System.Text.Json;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 模式
    /// </summary>
    public enum AgentMode
    {
        /// <summary>主 Agent - 用户交互</summary>
        Primary,
        
        /// <summary>子 Agent - 被其他 Agent 调用</summary>
        SubAgent,
        
        /// <summary>通用 - 可作为主 Agent 或子 Agent</summary>
        All
    }

    /// <summary>
    /// Agent 上下文
    /// </summary>
    public class AgentContext
    {
        public string SessionId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public CancellationToken CancellationToken { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Agent 结果
    /// </summary>
    public class AgentResult
    {
        public bool Success { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public string Output { get; set; } = string.Empty;
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Agent 接口 - AI Agent 的核心抽象
    /// </summary>
    public interface IAgent
    {
        /// <summary>Agent 名称</summary>
        string Name { get; }
        
        /// <summary>Agent 模式</summary>
        AgentMode Mode { get; }
        
        /// <summary>Agent 描述</summary>
        string Description { get; }
        
        /// <summary>权限规则集</summary>
        IReadOnlyList<PermissionRule> Permissions { get; }
        
        /// <summary>系统提示词</summary>
        string? SystemPrompt { get; }
        
        /// <summary>模型配置</summary>
        ModelReference? Model { get; }
        
        /// <summary>最大迭代步骤</summary>
        int? MaxSteps { get; }
        
        /// <summary>执行 Agent</summary>
        IAsyncEnumerable<ChatMessage> ExecuteAsync(
            ChatMessage input,
            AgentContext context,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 模型引用
    /// </summary>
    public class ModelReference
    {
        public string ProviderId { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
    }
}