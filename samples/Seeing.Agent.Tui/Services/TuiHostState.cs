namespace Seeing.Agent.Tui.Services;

/// <summary>
/// TUI 运行时状态（工作区、当前 Agent、规则文本、已注册的 MCP 工具 Id）。
/// </summary>
public sealed class TuiHostState
{
    public string WorkspaceRoot { get; set; } = "";

    public string CurrentAgentKey { get; set; } = "primary";

    public string RulesMarkdown { get; set; } = "";

    public IReadOnlyList<string> RulesSources { get; set; } = Array.Empty<string>();

    /// <summary>本轮 MCP 连接注册到 <see cref="Seeing.Agent.Tools.ToolInvoker"/> 的工具 Id，便于 /cd 时卸载。</summary>
    public List<string> RegisteredMcpToolIds { get; } = new();

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
}
