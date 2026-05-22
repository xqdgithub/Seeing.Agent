using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Core.Generation;

namespace Seeing.Agent.Core.Generation
{
    /// <summary>
    /// Agent 动态生成器接口
    /// 根据模板和配置动态生成 Agent 实例
    /// </summary>
    public interface IAgentGenerator
    {
        /// <summary>根据模板生成 Agent 配置</summary>
        Task<AgentDefinition> GenerateAsync(AgentGenerationRequest request, CancellationToken cancellationToken = default);

        /// <summary>验证 Agent 定义是否有效</summary>
        Task<AgentValidationResult> ValidateAsync(AgentDefinition definition, CancellationToken cancellationToken = default);

        /// <summary>列出所有可用模板</summary>
        Task<IReadOnlyList<AgentTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default);

        /// <summary>获取模板</summary>
        Task<AgentTemplate?> GetTemplateAsync(string templateId, CancellationToken cancellationToken = default);

        /// <summary>注册自定义模板</summary>
        Task RegisterTemplateAsync(AgentTemplate template, CancellationToken cancellationToken = default);

        /// <summary>从现有 Agent 定义创建模板</summary>
        Task<AgentTemplate> ExtractTemplateAsync(AgentDefinition definition, string templateName, CancellationToken cancellationToken = default);
    }

    /// <summary>Agent 生成请求</summary>
    public class AgentGenerationRequest
    {
        /// <summary>模板 ID（可选，不指定则使用默认模板）</summary>
        public string? TemplateId { get; set; }

        /// <summary>Agent 名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Agent 描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>系统提示词</summary>
        public string SystemPrompt { get; set; } = string.Empty;

        /// <summary>允许的工具列表（空表示全部允许）</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>禁止的工具列表</summary>
        public List<string> DeniedTools { get; set; } = new();

        /// <summary>模型配置覆盖</summary>
        public ModelConfigOverride? ModelOverride { get; set; }

        /// <summary>最大迭代次数</summary>
        public int? MaxIterations { get; set; }

        /// <summary>超时（秒）</summary>
        public int? TimeoutSeconds { get; set; }

        /// <summary>自定义变量（用于模板渲染）</summary>
        public Dictionary<string, string> Variables { get; set; } = new();

        /// <summary>标签</summary>
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>模型配置覆盖</summary>
    public class ModelConfigOverride
    {
        public string? Provider { get; set; }
        public string? ModelId { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public double? TopP { get; set; }
    }
}
