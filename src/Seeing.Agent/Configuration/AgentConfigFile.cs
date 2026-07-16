using Seeing.Agent.Core.Permission;
using YamlDotNet.Serialization;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// MD 配置文件模型 - 对应 YAML Front Matter
    /// </summary>
    public class AgentConfigFile
    {
        // === 基础配置 ===
        
        /// <summary>Agent 名称（必需，与文件名一致）</summary>
        public string? Name { get; set; }
        
        /// <summary>Agent 描述</summary>
        public string? Description { get; set; }
        
        /// <summary>运行模式：Primary / SubAgent / All</summary>
        public string? Mode { get; set; }
        
        /// <summary>分类标签</summary>
        public string? Category { get; set; }
        
        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }
        

        
        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; set; }
        
        /// <summary>是否隐藏（不在用户选择列表显示，但仍可作为 SubAgent 使用）</summary>
        public bool? IsHidden { get; set; }

        /// <summary>是否禁用（完全不可用）</summary>
        public bool? Disabled { get; set; }
        
        // === 权限配置 ===
        
        /// <summary>权限规则列表</summary>
        public List<PermissionRuleEntry>? PermissionRules { get; set; }
        
        /// <summary>权限默认效果：Allow / Deny / Ask</summary>
        public string? PermissionDefaultEffect { get; set; }
        
        // === 工具限制 ===
        
        /// <summary>允许的工具列表（空数组表示全部允许）</summary>
        public List<string>? AllowedTools { get; set; }
        
        /// <summary>禁止的工具列表</summary>
        public List<string>? DeniedTools { get; set; }
        
        // === 模型配置 ===
        
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
        /// <b>默认行为：</b>如果未设置（null），系统会自动使用模型的 output limit。
        /// </para>
        /// <para>
        /// <b>使用场景：</b>仅在需要强制使用比模型限制更小的输出时设置此值。
        /// </para>
        /// </summary>
        public int? MaxTokens { get; set; }
        
        // === SystemPrompt（从 Markdown Body 解析）===
        
        /// <summary>系统提示词（Markdown Body 内容）</summary>
        [YamlIgnore]
        public string? SystemPrompt { get; set; }
    }
}
