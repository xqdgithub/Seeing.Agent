using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// Agent 运行时上下文 - 管理 Agent、模型、会话和资源配置
/// </summary>
public class AgentContext
{
    // ========== 工作区 ==========

    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>当前Agent键</summary>
    public string CurrentAgentKey { get; set; } = "primary";

    // ========== 会话 ==========

    /// <summary>会话ID</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    // ========== 配置信息 ==========

    /// <summary>Agent配置字典</summary>
    public Dictionary<string, AgentConfig> AgentConfigs { get; set; } = new();

    /// <summary>当前模型</summary>
    public string? CurrentModel { get; set; }

    /// <summary>规则Markdown文本</summary>
    public string RulesMarkdown { get; set; } = "";

    /// <summary>规则来源列表</summary>
    public IReadOnlyList<string> RulesSources { get; set; } = Array.Empty<string>();

    // ========== 工具状态 ==========

    /// <summary>已注册的 MCP 工具 ID 列表</summary>
    public List<string> RegisteredMcpToolIds { get; } = new();

    /// <summary>工具数量</summary>
    public int ToolCount { get; set; }

    /// <summary>技能数量</summary>
    public int SkillCount { get; set; }

    /// <summary>MCP服务器数量</summary>
    public int McpServerCount { get; set; }

    /// <summary>扩展数量</summary>
    public int ExtensionCount { get; set; }

    // ========== 详细信息列表 ==========

    /// <summary>技能详细信息列表</summary>
    public IReadOnlyDictionary<string, SkillInfo> SkillInfos { get; set; } = new Dictionary<string, SkillInfo>();

    /// <summary>工具详细信息列表</summary>
    public IReadOnlyCollection<ITool> ToolInfos { get; set; } = Array.Empty<ITool>();

    /// <summary>MCP服务器详细信息列表（服务器名称）</summary>
    public IReadOnlyCollection<string> McpServerNames { get; set; } = Array.Empty<string>();

    /// <summary>MCP工具详细信息列表</summary>
    public IReadOnlyCollection<McpTool> McpToolInfos { get; set; } = Array.Empty<McpTool>();
}