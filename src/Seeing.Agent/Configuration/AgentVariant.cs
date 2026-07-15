using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 变体配置 - 用于 Provider/Model 特定的配置覆盖
    /// </summary>
    public class AgentVariant
    {
        /// <summary>模型 ID</summary>
        public string? Model { get; set; }
        
        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }
        
        /// <summary>TopP 参数</summary>
        public double? TopP { get; set; }
        
        /// <summary>最大输出 Token 数</summary>
        public int? MaxTokens { get; set; }
        
        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }
        
        /// <summary>权限规则</summary>
        public List<PermissionRuleEntry>? PermissionRules { get; set; }
        
        /// <summary>允许的工具（白名单）</summary>
        public List<string>? AllowedTools { get; set; }
        
        /// <summary>禁止的工具（黑名单）</summary>
        public List<string>? DeniedTools { get; set; }
        
        /// <summary>完全替换基础 SystemPrompt</summary>
        public string? SystemPrompt { get; set; }
        
        /// <summary>插入到基础 SystemPrompt 开头</summary>
        public string? SystemPromptPrepend { get; set; }
        
        /// <summary>追加到基础 SystemPrompt 末尾</summary>
        public string? SystemPromptAppend { get; set; }
    }
}
