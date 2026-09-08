using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Configuration
{
    /// <summary>
    /// Seeing.Agent 脊柱配置选项（modules/seams/agents/permission/models/workspace + 核心工具/标题）。
    /// 子系统 Options（Gateway/Acp/TokenBudget/Skills/Shell/Mcp 等）已迁至各自能力包。
    /// </summary>
    public class SeeingAgentOptions
    {
        /// <summary>默认模型</summary>
        public string? DefaultModel { get; set; }

        /// <summary>默认 Agent</summary>
        public string? DefaultAgent { get; set; }

        /// <summary>进程级场景名（seeing.json <c>scenario</c>）；null 回退 Host Shape 默认。</summary>
        public string? Scenario { get; set; }

        /// <summary>
        /// 自定义/覆盖场景字典（seeing.json <c>Scenarios</c>）。
        /// key 为场景名；可新增自定义，或覆盖同名内置预设的 modules/agent/seams/tools。
        /// </summary>
        public Dictionary<string, ScenarioConfig> Scenarios { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>进程级模块装配覆盖（seeing.json <c>modules</c>）。</summary>
        public ModulesOptions Modules { get; set; } = new();

        /// <summary>
        /// 进程级 seam 绑定覆盖（seeing.json <c>seams</c>）：seam 名 → 提供方模块 id
        /// （如 <c>executionWorld</c>→<c>io.local</c>）。覆盖 scenario seams；禁止用逻辑名 switch。
        /// </summary>
        public Dictionary<string, string> Seams { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>权限配置</summary>
        public PermissionOptions Permission { get; set; } = new();

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

        /// <summary>Agent 特定的模型配置（Agent 名称 → 模型 ID，格式 provider/model）</summary>
        public Dictionary<string, string> AgentModels { get; set; } = new();

        /// <summary>全局默认工作区路径（仅用户级）</summary>
        public string? GlobalWorkspaceRoot { get; set; }

        /// <summary>工作区配置</summary>
        public WorkspaceOptions Workspace { get; set; } = new();

        /// <summary>
        /// 工具执行全局兜底超时（默认 null 关闭）
        /// <para>
        /// 启用后，ToolTimeoutDecorator（工具执行漏斗内）对每个工具调用施加该超时，超时统一报告为"执行超时"。
        /// 仅用于兜底第三方无内部超时的工具；内置工具（bash/web/git/MCP）均有自身超时。
        /// 工具可经能力声明豁免（timeout.skip=true）或指定自身上限（timeout.budget 毫秒）。
        /// </para>
        /// </summary>
        public TimeSpan? ToolExecutionTimeout { get; set; }

        /// <summary>工具输出限制配置（超限落盘到会话 ref 目录 + 头尾预览）</summary>
        public ToolOutputOptions ToolOutput { get; set; } = new();

        /// <summary>会话标题自动生成配置</summary>
        public TitleGenerationOptions TitleGeneration { get; set; } = new();
    }

    /// <summary>
    /// 会话标题自动生成选项
    /// </summary>
    public sealed class TitleGenerationOptions
    {
        /// <summary>是否启用自动生成</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>标题生成专用模型（可选，未设置时使用 fallback）</summary>
        public string? Model { get; set; }

        /// <summary>LLM 调用超时（秒）</summary>
        public int TimeoutSeconds { get; set; } = 60;
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
    /// 工具输出限制选项
    /// <para>工具输出超过 <see cref="MaxInlineBytes"/> 时：全文落盘到会话 ref 目录，
    /// Output 替换为 &lt;persisted-output&gt; 头+尾预览，完整内容可经 read 工具读取。</para>
    /// </summary>
    public sealed class ToolOutputOptions
    {
        /// <summary>是否启用输出限制（默认 true）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>内联上限（UTF8 字节）。输出超过此值触发落盘+预览。对齐 FileSystemHelper.MaxBytes。</summary>
        public int MaxInlineBytes { get; set; } = 50 * 1024;

        /// <summary>预览头部字符数</summary>
        public int PreviewHeadChars { get; set; } = 4 * 1024;

        /// <summary>预览尾部字符数</summary>
        public int PreviewTailChars { get; set; } = 2 * 1024;
    }
}
