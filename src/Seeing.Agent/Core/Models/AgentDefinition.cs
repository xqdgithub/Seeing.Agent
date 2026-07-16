using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Agent 定义 - 代理的完整元数据定义
    /// <para>
    /// Agent 只需定义元数据，执行逻辑由 AgentExecutor 统一处理。
    /// 这使得 Agent 成为"Prompt Engineering"而非代码实现。
    /// </para>
    /// <para>
    /// 此类合并了原 AgentInfo 和 AgentDefinition 的职责，统一 Agent 配置模型。
    /// </para>
    /// </summary>
    public class AgentDefinition
    {
        // === 基础标识 ===
        
        /// <summary>Agent 名称（唯一标识）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Agent 描述</summary>
        public string? Description { get; set; }

        /// <summary>Agent 模式</summary>
        public AgentMode Mode { get; set; } = AgentMode.All;

        /// <summary>Agent 类别（用于委托分类，如 deep、quick、visual-engineering）</summary>
        public string? Category { get; set; }
        
        /// <summary>Agent 标签</summary>
        public List<string> Tags { get; set; } = new();

        // === 运行时标识 ===
        
        /// <summary>是否为内置 Agent</summary>
        public bool IsNative { get; set; }
        
        /// <summary>是否隐藏（不在用户选择列表显示，但仍可作为 SubAgent 使用）</summary>
        public bool IsHidden { get; set; }
        
        /// <summary>是否禁用（完全不可用，任何地方都无法使用）</summary>
        public bool Disabled { get; set; }
        
        /// <summary>是否为后台 Agent（异步执行）</summary>
        public bool IsBackground { get; set; }
        
        // === 核心配置 ===
        
        /// <summary>系统提示词（核心配置）</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>模型引用</summary>
        public ModelReference? Model { get; set; }

        /// <summary>最大迭代步骤</summary>
        public int? MaxSteps { get; set; }

        // === LLM 参数 ===
        
        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }

        /// <summary>TopP 参数</summary>
        public double? TopP { get; set; }

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
        public int? MaxTokens { get; set; }

        // === 权限配置 ===
        
        /// <summary>权限规则</summary>
        public List<PermissionRuleEntry> PermissionRules { get; set; } = new();

        /// <summary>权限默认效果（当没有匹配规则时，默认询问用户）</summary>
        public PermissionEffect PermissionDefaultEffect { get; set; } = PermissionEffect.Ask;

        // === 工具限制 ===
        
        /// <summary>允许的工具（白名单，空表示允许所有）</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>禁止的工具（黑名单）</summary>
        public List<string> DeniedTools { get; set; } = new();

        /// <summary>允许的 MCP 服务器</summary>
        public List<string> AllowedMcpServers { get; set; } = new();

        /// <summary>允许的子代理</summary>
        public List<string> AllowedAgents { get; set; } = new();

        // === 执行配置 ===
        
        /// <summary>执行运行时类型</summary>
        public AgentRuntime Runtime { get; set; } = AgentRuntime.Native;

        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; set; }

        /// <summary>
        /// Token 预算配置（覆盖全局配置）
        /// </summary>
        public TokenBudgetConfig? BudgetConfig { get; set; }

        // === 扩展 ===
        
        /// <summary>
        /// 创建 IAgent 实例（延迟创建，用于执行）
        /// </summary>
        public Func<IAgent>? AgentFactory { get; set; }

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
        public static AgentDefinition FromAgent(IAgent agent)
        {
            return new AgentDefinition
            {
                Name = agent.Name,
                Description = agent.Description,
                Mode = agent.Mode,
                SystemPrompt = agent.SystemPrompt,
                Model = agent.Model,
                MaxSteps = agent.MaxSteps,
                Temperature = agent.Temperature,
                TopP = agent.TopP,
                MaxTokens = agent.MaxTokens,
                AllowedTools = agent.AllowedTools.ToList(),
                DeniedTools = agent.DeniedTools.ToList(),
                PermissionRules = agent.PermissionRules.ToList(),
                PermissionDefaultEffect = agent.PermissionDefaultEffect,
                IsHidden = agent.Status == AgentStatus.Disabled,
                Disabled = agent.Disabled,
                Runtime = agent.Runtime,
                AcpBackend = agent.AcpBackend
            };
        }
    }
}
