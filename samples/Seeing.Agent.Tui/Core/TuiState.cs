using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Tui.Core.State;

namespace Seeing.Agent.Tui.Core;

/// <summary>
/// TUI 全局状态管理 - 向后兼容转发类，实际实现见 State.TuiState
/// </summary>
public class TuiState : State.TuiState
{
    // ========== 输入状态代理（可设置） ==========

    public new bool IsProcessing { get => Input.IsProcessing; set => Input.IsProcessing = value; }

    // ========== Agent 属性代理（向后兼容） ==========

    public new string WorkspaceRoot { get => Agent.WorkspaceRoot; set => Agent.WorkspaceRoot = value; }
    public string CurrentAgentKey { get => Agent.CurrentAgentKey; set => Agent.CurrentAgentKey = value; }
    public new string? CurrentModel { get => Agent.CurrentModel; set => Agent.CurrentModel = value; }
    public string RulesMarkdown { get => Agent.RulesMarkdown; set => Agent.RulesMarkdown = value; }
    public IReadOnlyList<string> RulesSources { get => Agent.RulesSources; set => Agent.RulesSources = value; }

    // ========== 工具状态代理 ==========

    public List<string> RegisteredMcpToolIds => Agent.RegisteredMcpToolIds;
    public int ToolCount { get => Agent.ToolCount; set => Agent.ToolCount = value; }
    public int SkillCount { get => Agent.SkillCount; set => Agent.SkillCount = value; }
    public int McpServerCount { get => Agent.McpServerCount; set => Agent.McpServerCount = value; }
    public int ExtensionCount { get => Agent.ExtensionCount; set => Agent.ExtensionCount = value; }

    // ========== 详细信息代理 ==========

    public IReadOnlyDictionary<string, SkillInfo> SkillInfos { get => Agent.SkillInfos; set => Agent.SkillInfos = value; }
    public IReadOnlyCollection<ITool> ToolInfos { get => Agent.ToolInfos; set => Agent.ToolInfos = value; }
    public IReadOnlyCollection<string> McpServerNames { get => Agent.McpServerNames; set => Agent.McpServerNames = value; }
    public IReadOnlyCollection<McpTool> McpToolInfos { get => Agent.McpToolInfos; set => Agent.McpToolInfos = value; }

    // ========== 流式消息代理 ==========

    public StreamingMessage? CurrentStreamingMessage
    {
        get => Messages.CurrentStreamingMessage;
        set { Messages.CurrentStreamingMessage = value; OnStateChanged(State.RenderRegion.Streaming); }
    }

    public List<ToolCallDisplay> CurrentToolCalls => Messages.CurrentToolCalls;
    public int MaxMessages { get => Messages.MaxMessages; set => Messages.MaxMessages = value; }

    // ========== 导航状态代理 ==========

    public string? SearchKeyword => Navigation.SearchKeyword;
    public List<int> SearchMatchIndices => Navigation.SearchMatchIndices;
    public int CurrentSearchMatchIndex => Navigation.CurrentSearchMatchIndex;
    public bool IsSearchMode => Navigation.IsSearchMode;
    public HashSet<string> FoldedMessageIds => Navigation.FoldedMessageIds;
    public int HighlightedMessageIndex => Navigation.HighlightedMessageIndex;

    // ========== 导航方法 ==========

    public void ClearSearch() => Navigation.ClearSearch();
    public void ToggleMessageFold(string messageId) => Navigation.ToggleFold(messageId);
}