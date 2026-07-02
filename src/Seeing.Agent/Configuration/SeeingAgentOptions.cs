using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
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

        /// <summary>权限配置</summary>
        public PermissionOptions Permission { get; set; } = new();

        /// <summary>Gateway 配置</summary>
        public GatewayOptions Gateway { get; set; } = new();

        /// <summary>
        /// 插件列表
        /// <para>
        /// 支持格式：
        /// - NuGet 包名：@seeing/analytics@1.0.0
        /// - 文件路径：./plugins/MyExtension.dll
        /// - file:// URL：file://./plugins/MyExtension.dll
        /// </para>
        /// </summary>
        public List<PluginSpec> Plugins { get; set; } = new();

        /// <summary>
        /// 插件启用状态覆盖
        /// <para>
        /// key: 插件 ID（NuGet 包名或文件名）
        /// value: true 启用，false 禁用
        /// </para>
        /// </summary>
        public Dictionary<string, bool> PluginEnabled { get; set; } = new();
    }

    /// <summary>
    /// 权限配置选项
    /// </summary>
    public class PermissionOptions
    {
        /// <summary>
        /// 自动批准所有权限请求（危险！）
        /// <para>
        /// 安全警告：启用此选项后，Agent 将在无需用户确认的情况下执行所有操作，
        /// 包括文件写入、命令执行、工具调用等。仅建议在受控环境中使用。
        /// </para>
        /// <para>
        /// 默认值：false（需要用户确认）
        /// </para>
        /// </summary>
        public bool AutoApproveAll { get; set; } = false;
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
        /// <summary>是否禁用</summary>
        public bool Disable { get; set; }

        /// <summary>使用的提供商 ID</summary>
        public string? Provider { get; set; }

        /// <summary>使用的模型 ID</summary>
        public string? Model { get; set; }

        /// <summary>变体标识</summary>
        public string? Variant { get; set; }

        /// <summary>系统提示词</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>描述</summary>
        public string? Description { get; set; }

        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }

        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }

        /// <summary>TopP 参数</summary>
        public double? TopP { get; set; }

        /// <summary>最大 Token 数</summary>
        public int? MaxTokens { get; set; }

        /// <summary>Agent 模式</summary>
        public AgentMode? Mode { get; set; }

        /// <summary>颜色标识（UI 显示）</summary>
        public string? Color { get; set; }

        /// <summary>是否隐藏（不在 UI 中显示）</summary>
        public bool? IsHidden { get; set; }

        /// <summary>权限配置（旧格式，保留兼容）</summary>
        public Dictionary<string, object>? Permissions { get; set; }

        /// <summary>权限规则（新格式）</summary>
        public List<PermissionRuleEntry>? PermissionRules { get; set; }

        /// <summary>额外设置</summary>
        public Dictionary<string, object>? Options { get; set; }
    }
}