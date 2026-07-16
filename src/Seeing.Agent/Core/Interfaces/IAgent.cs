using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 接口 - AI Agent 的核心抽象
    /// <para>
    /// 定义 Agent 的元数据和执行能力：
    /// - 元数据：Name、Mode、Description、Permissions、SystemPrompt、Model、MaxSteps
    /// - 执行：ExecuteAsync 返回流式消息序列
    /// </para>
    /// </summary>
    public interface IAgent
    {
        /// <summary>Agent 名称（唯一标识）</summary>
        string Name { get; set; }

        /// <summary>Agent 模式（Primary/SubAgent/All）</summary>
        AgentMode Mode { get; set; }

        /// <summary>Agent 描述</summary>
        string Description { get; set; }

        /// <summary>权限规则集（新格式）</summary>
        IReadOnlyList<PermissionRuleEntry> PermissionRules { get; set; }

        /// <summary>系统提示词</summary>
        string? SystemPrompt { get; set; }

        /// <summary>模型配置</summary>
        ModelReference? Model { get; set; }

        /// <summary>最大迭代步骤</summary>
        int? MaxSteps { get; set; }

        /// <summary>温度参数</summary>
        double? Temperature { get; set; }

        /// <summary>TopP 参数</summary>
        double? TopP { get; set; }

        /// <summary>最大输出 Token 数</summary>
        int? MaxTokens { get; set; }

        /// <summary>Agent 状态</summary>
        AgentStatus Status { get; set; }

        /// <summary>是否禁用（完全不可用）</summary>
        bool Disabled { get; set; }

        /// <summary>允许使用的工具列表（白名单）</summary>
        IReadOnlyList<string> AllowedTools { get; set; }

        /// <summary>禁止使用的工具列表（黑名单）</summary>
        IReadOnlyList<string> DeniedTools { get; set; }

        /// <summary>权限默认效果（当没有匹配规则时）</summary>
        PermissionEffect PermissionDefaultEffect { get; set; }

        /// <summary>执行运行时类型</summary>
        AgentRuntime Runtime { get; set; }

        /// <summary>ACP 后端标识</summary>
        string? AcpBackend { get; set; }

        /// <summary>
        /// 执行 Agent
        /// </summary>
        /// <param name="input">输入消息</param>
        /// <param name="context">执行上下文</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>流式消息序列</returns>
        IAsyncEnumerable<ChatMessage> ExecuteAsync(
            ChatMessage input,
            AgentContext context,
            CancellationToken cancellationToken = default);
    }
}