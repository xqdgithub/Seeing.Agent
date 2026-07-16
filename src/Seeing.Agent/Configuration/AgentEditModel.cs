using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 编辑模型 - 用于 UI 编辑，统一封装 Agent 的可编辑属性
    /// </summary>
    public class AgentEditModel
    {
        /// <summary>Agent 名称（只读，用于标识）</summary>
        public string Name { get; set; } = "";

        /// <summary>Agent 描述</summary>
        public string? Description { get; set; }

        /// <summary>运行模式</summary>
        public AgentMode Mode { get; set; } = AgentMode.All;

        /// <summary>分类标签</summary>
        public string? Category { get; set; }

        /// <summary>系统提示词</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>默认 Provider ID</summary>
        public string? Provider { get; set; }

        /// <summary>默认模型 ID</summary>
        public string? Model { get; set; }

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

        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }

        /// <summary>是否隐藏（不在用户选择列表显示，但仍可作为 SubAgent 使用）</summary>
        public bool IsHidden { get; set; }

        /// <summary>是否禁用（完全不可用）</summary>
        public bool Disabled { get; set; }

        /// <summary>权限默认效果</summary>
        public PermissionEffect PermissionDefaultEffect { get; set; } = PermissionEffect.Ask;

        /// <summary>权限规则列表</summary>
        public List<PermissionRuleEntry> PermissionRules { get; set; } = new();

        /// <summary>允许的工具列表</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>禁止的工具列表</summary>
        public List<string> DeniedTools { get; set; } = new();

        /// <summary>是否为内置 Agent</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>是否已有 MD 覆盖配置</summary>
        public bool HasMdOverride { get; set; }

        /// <summary>MD 配置存在的层级（如果有）</summary>
        public ConfigLevel? MdConfigLevel { get; set; }
    }
}
