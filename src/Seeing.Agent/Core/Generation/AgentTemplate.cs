using System;
using System.Collections.Generic;

namespace Seeing.Agent.Core.Generation
{
    /// <summary>
    /// Agent 模板定义
    /// </summary>
    public class AgentTemplate
    {
        /// <summary>模板 ID</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>模板名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>模板描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>模板版本</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>模板作者</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>分类</summary>
        public string Category { get; set; } = "general";

        /// <summary>标签</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>系统提示词模板（支持 {{Variable}} 占位符）</summary>
        public string SystemPromptTemplate { get; set; } = string.Empty;

        /// <summary>默认允许的工具</summary>
        public List<string> DefaultAllowedTools { get; set; } = new();

        /// <summary>默认禁止的工具</summary>
        public List<string> DefaultDeniedTools { get; set; } = new();

        /// <summary>默认模型配置</summary>
        public ModelConfigOverride? DefaultModelOverride { get; set; }

        /// <summary>默认最大迭代次数</summary>
        public int DefaultMaxIterations { get; set; } = 10;

        /// <summary>默认超时（秒）</summary>
        public int DefaultTimeoutSeconds { get; set; } = 300;

        /// <summary>所需变量定义</summary>
        public List<TemplateVariable> RequiredVariables { get; set; } = new();

        /// <summary>是否为内置模板</summary>
        public bool IsBuiltin { get; set; }

        /// <summary>创建时间</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>更新时间</summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>模板变量定义</summary>
    public class TemplateVariable
    {
        /// <summary>变量名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>默认值</summary>
        public string? DefaultValue { get; set; }

        /// <summary>是否必须</summary>
        public bool IsRequired { get; set; }

        /// <summary>验证正则</summary>
        public string? ValidationPattern { get; set; }
    }

    /// <summary>Agent 定义（生成结果）</summary>
    public class AgentDefinition
    {
        /// <summary>Agent ID</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>Agent 名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Agent 描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>系统提示词（已渲染）</summary>
        public string SystemPrompt { get; set; } = string.Empty;

        /// <summary>允许的工具</summary>
        public List<string> AllowedTools { get; set; } = new();

        /// <summary>禁止的工具</summary>
        public List<string> DeniedTools { get; set; } = new();

        /// <summary>模型配置</summary>
        public ModelConfigOverride? ModelConfig { get; set; }

        /// <summary>最大迭代次数</summary>
        public int MaxIterations { get; set; } = 10;

        /// <summary>超时（秒）</summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>来源模板 ID</summary>
        public string? SourceTemplateId { get; set; }

        /// <summary>标签</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>创建时间</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>是否有效</summary>
        public bool IsActive { get; set; } = true;
    }
}
