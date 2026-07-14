using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;

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

        /// <summary>温度参数</summary>
        public double? Temperature { get; init; }

        /// <summary>TopP 参数</summary>
        public double? TopP { get; init; }

        /// <summary>
        /// 最大输出 Token 数（可选覆盖）
        /// <para>
        /// <b>注意：</b>这是可选的覆盖值，用于在特定场景下手动限制输出长度。
        /// </para>
        /// <para>
        /// <b>默认行为：</b>如果未设置（null），系统会自动使用模型的 output limit
        /// （即 <see cref="Llm.ModelConfig.Limit.Output"/>）。例如：
        /// <list type="bullet">
        ///   <item><description>Kimi-K2.5: 32000 tokens</description></item>
        ///   <item><description>GLM-4.7: 128000 tokens</description></item>
        ///   <item><description>Claude Sonnet 4: 16000 tokens</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>使用场景：</b>仅在需要强制使用比模型限制更小的输出时设置此值。
        /// 例如：快速响应 Agent 只需要 2000 tokens。
        /// </para>
        /// <para>
        /// <b>注意：</b>设置超过模型限制的值会被自动约束到模型限制。
        /// </para>
        /// </summary>
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

        /// <summary>
        /// Token 预算配置（覆盖全局配置）
        /// </summary>
        public TokenBudgetConfig? BudgetConfig { get; init; }

        /// <summary>执行运行时类型</summary>
        public AgentRuntime Runtime { get; init; } = AgentRuntime.Native;

        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; init; }

        /// <summary>权限默认效果（当没有匹配规则时，默认询问用户）</summary>
        public PermissionEffect PermissionDefaultEffect { get; init; } = PermissionEffect.Ask;

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
                PermissionRules = agent.PermissionRules,
                PermissionDefaultEffect = agent.PermissionDefaultEffect,
                IsHidden = agent.Status == AgentStatus.Disabled,
                Runtime = agent.Runtime,
                AcpBackend = agent.AcpBackend
            };
        }
    }
}