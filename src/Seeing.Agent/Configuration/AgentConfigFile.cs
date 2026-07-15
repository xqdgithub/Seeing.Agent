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
        
        /// <summary>运行时类型：Native / AcpPassthrough</summary>
        public string? Runtime { get; set; }
        
        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; set; }
        
        /// <summary>是否隐藏</summary>
        public bool? IsHidden { get; set; }
        
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
        
        /// <summary>最大输出 Token 数</summary>
        public int? MaxTokens { get; set; }
        
        // === 变体定义 ===
        
        /// <summary>Provider/Model 变体映射</summary>
        public Dictionary<string, AgentVariant>? Variants { get; set; }
        
        // === SystemPrompt（从 Markdown Body 解析）===
        
        /// <summary>系统提示词（Markdown Body 内容）</summary>
        [YamlIgnore]
        public string? SystemPrompt { get; set; }
    }
}
