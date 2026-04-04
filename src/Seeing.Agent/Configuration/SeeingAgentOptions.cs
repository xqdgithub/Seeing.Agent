using Seeing.Agent.Llm;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Seeing.Agent 配置选项
    /// </summary>
    public class SeeingAgentOptions
    {
        /// <summary>默认模型</summary>
        public string? DefaultModel { get; set; }

        /// <summary>默认 Provider</summary>
        public string? DefaultProvider { get; set; }

        /// <summary>默认 Agent</summary>
        public string? DefaultAgent { get; set; }

        /// <summary>全局模型目录（与 ModelScope 单模型条目结构一致：modalities、limit、options）</summary>
        public Dictionary<string, ModelConfig>? Models { get; set; }

        /// <summary>ModelScope 风格块（与 <see cref="Models"/> 合并，通常包含 models 字典）</summary>
        public ModelScopeSection? ModelScope { get; set; }

        /// <summary>Provider 配置列表</summary>
        public Dictionary<string, ProviderConfig> Providers { get; set; } = new();

        /// <summary>Agent 配置</summary>
        public Dictionary<string, AgentConfig> Agents { get; set; } = new();

        /// <summary>技能配置</summary>
        public SkillsConfig Skills { get; set; } = new();

        /// <summary>插件列表</summary>
        public List<string> Plugins { get; set; } = new();
    }

    /// <summary>
    /// 技能配置
    /// </summary>
    public class SkillsConfig
    {
        /// <summary>本地技能路径列表</summary>
        public List<string> Paths { get; set; } = new();

        /// <summary>远程技能 URL 列表（index.json 格式）</summary>
        public List<string> Urls { get; set; } = new();
    }

    /// <summary>
    /// ModelScope 兼容配置节（JSON: SeeingAgent:ModelScope）
    /// </summary>
    public class ModelScopeSection
    {
        /// <summary>模型 ID → 模型定义（与 Provider.models 条目相同）</summary>
        public Dictionary<string, ModelConfig>? Models { get; set; }
    }

    /// <summary>
    /// Agent 配置（seeing.json 格式）
    /// </summary>
    public class AgentConfig
    {
        /// <summary>使用的提供商 ID</summary>
        public string? Provider { get; set; }

        /// <summary>使用的模型 ID</summary>
        public string? Model { get; set; }

        /// <summary>系统提示词</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }

        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }

        /// <summary>最大 Token 数</summary>
        public int? MaxTokens { get; set; }

        /// <summary>额外设置</summary>
        public Dictionary<string, object>? Options { get; set; }
    }
}