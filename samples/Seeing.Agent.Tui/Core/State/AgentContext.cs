using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// Agent 运行时上下文 - 管理 Agent、模型、会话和资源配置（线程安全）
/// </summary>
public class AgentContext
{
    private readonly object _lock = new();
    private string _workspaceRoot = "";
    private string _currentAgentKey = "primary";
    private string? _currentModel;
    private string _rulesMarkdown = "";

    // ========== 工作区 ==========

    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot
    {
        get { lock (_lock) return _workspaceRoot; }
        set { lock (_lock) _workspaceRoot = value; }
    }

    /// <summary>当前Agent键</summary>
    public string CurrentAgentKey
    {
        get { lock (_lock) return _currentAgentKey; }
        set { lock (_lock) _currentAgentKey = value; }
    }

    /// <summary>会话ID</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    // ========== 配置信息 ==========

    /// <summary>当前模型</summary>
    public string? CurrentModel
    {
        get { lock (_lock) return _currentModel; }
        set { lock (_lock) _currentModel = value; }
    }

    /// <summary>规则Markdown文本</summary>
    public string RulesMarkdown
    {
        get { lock (_lock) return _rulesMarkdown; }
        set { lock (_lock) _rulesMarkdown = value; }
    }

    /// <summary>规则来源列表</summary>
    public IReadOnlyList<string> RulesSources { get; set; } = Array.Empty<string>();

    // ========== 工具状态（只读统计） ==========

    /// <summary>工具数量</summary>
    public int ToolCount { get; set; }

    /// <summary>技能数量</summary>
    public int SkillCount { get; set; }

    /// <summary>MCP服务器数量</summary>
    public int McpServerCount { get; set; }

    /// <summary>扩展数量</summary>
    public int ExtensionCount { get; set; }

    /// <summary>已注册的 MCP 工具 ID 列表</summary>
    public List<string> RegisteredMcpToolIds { get; } = new();

    // ========== 详细信息列表 ==========

    /// <summary>技能详细信息</summary>
    public IReadOnlyDictionary<string, SkillInfo> SkillInfos { get; set; } = new Dictionary<string, SkillInfo>();

    /// <summary>工具详细信息</summary>
    public IReadOnlyCollection<ITool> ToolInfos { get; set; } = Array.Empty<ITool>();

    /// <summary>MCP服务器名称列表</summary>
    public IReadOnlyCollection<string> McpServerNames { get; set; } = Array.Empty<string>();

    /// <summary>MCP工具详细信息</summary>
    public IReadOnlyCollection<McpTool> McpToolInfos { get; set; } = Array.Empty<McpTool>();
}