using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Agent 定义 - 纯配置，参考 oh-my-openagent 设计
    /// <para>
    /// Agent 只需定义元数据，执行逻辑由 AgentExecutor 统一处理。
    /// 这使得 Agent 成为"Prompt Engineering"而非代码实现。
    /// </para>
    /// </summary>
    public class AgentDefinition
    {
        /// <summary>Agent 名称（唯一标识）</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Agent 描述</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Agent 模式</summary>
        public AgentMode Mode { get; init; } = AgentMode.All;

        /// <summary>Agent 类别（用于委托分类，如 deep、quick、visual-engineering）</summary>
        public string? Category { get; init; }

        /// <summary>系统提示词（核心配置）</summary>
        public string? SystemPrompt { get; init; }

        /// <summary>模型引用</summary>
        public ModelReference? Model { get; init; }

        /// <summary>最大迭代步骤</summary>
        public int? MaxSteps { get; init; }

        /// <summary>允许的工具（白名单，空表示允许所有）</summary>
        public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

        /// <summary>禁止的工具（黑名单）</summary>
        public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();

        /// <summary>权限规则</summary>
        public IReadOnlyList<PermissionRule> Permissions { get; init; } = Array.Empty<PermissionRule>();

        /// <summary>温度参数</summary>
        public double? Temperature { get; init; }

        /// <summary>TopP 参数</summary>
        public double? TopP { get; init; }

        /// <summary>最大 Token</summary>
        public int? MaxTokens { get; init; }

        /// <summary>是否隐藏（不在 UI 显示）</summary>
        public bool IsHidden { get; init; }

        /// <summary>UI 显示颜色</summary>
        public string? Color { get; init; }

        /// <summary>是否为后台 Agent（异步执行）</summary>
        public bool IsBackground { get; init; }

        /// <summary>权限策略引用（策略文件路径或策略 ID）</summary>
        public string? PermissionPolicy { get; init; }

        /// <summary>权限规则（新格式）</summary>
        public IReadOnlyList<PermissionRuleEntry> PermissionRules { get; init; } = Array.Empty<PermissionRuleEntry>();

        /// <summary>允许的 MCP 服务器</summary>
        public IReadOnlyList<string> AllowedMcpServers { get; init; } = Array.Empty<string>();

        /// <summary>允许的子代理</summary>
        public IReadOnlyList<string> AllowedAgents { get; init; } = Array.Empty<string>();

        /// <summary>权限默认效果</summary>
        public PermissionEffect PermissionDefaultEffect { get; init; } = PermissionEffect.Deny;

        /// <summary>
        /// 构建权限策略
        /// </summary>
        public AgentPermissionPolicy BuildPermissionPolicy()
        {
            return new AgentPermissionPolicy
            {
                AgentName = Name,
                Rules = PermissionRules,
                AllowedTools = AllowedTools,
                DeniedTools = DeniedTools,
                AllowedAgents = AllowedAgents,
                AllowedMcpServers = AllowedMcpServers,
                DefaultEffect = PermissionDefaultEffect
            };
        }

        /// <summary>
        /// 从 IAgent 实例创建定义
        /// </summary>
        public static AgentDefinition FromAgent(Interfaces.IAgent agent)
        {
            return new AgentDefinition
            {
                Name = agent.Name,
                Description = agent.Description,
                Mode = agent.Mode,
                SystemPrompt = agent.SystemPrompt,
                Model = agent.Model,
                MaxSteps = agent.MaxSteps,
                AllowedTools = agent.AllowedTools,
                DeniedTools = agent.DeniedTools,
                Permissions = agent.Permissions,
                IsHidden = agent.Status == AgentStatus.Disabled
            };
        }
    }
}