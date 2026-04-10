using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Tui.Core.State;

namespace Seeing.Agent.Tui.Core;

/// <summary>
/// TUI 全局状态管理 - 组合 MessageStore 和 AgentContext
/// </summary>
public class TuiState
{
    // ========== 组合的状态对象 ==========

    /// <summary>消息存储</summary>
    public MessageStore Messages { get; } = new();

    /// <summary>Agent 运行时上下文</summary>
    public State.AgentContext Agent { get; } = new();

    // ========== 输入状态 ==========

    /// <summary>是否多行模式</summary>
    public bool IsMultilineMode { get; set; } = false;

    /// <summary>是否正在处理</summary>
    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (_isProcessing != value)
            {
                _isProcessing = value;
                OnStateChanged(RenderRegion.Input);
            }
        }
    }

    /// <summary>当前任务的取消令牌源</summary>
    public CancellationTokenSource? CurrentTaskCts { get; set; }

    // ========== UI 状态 ==========

    /// <summary>消息滚动偏移</summary>
    public int ScrollOffset { get; set; } = 0;

    /// <summary>终端高度（最小10行，防止 RemoveRange 异常）</summary>
    public int TerminalHeight { get; set; } = 24; // 默认值，运行时更新

    /// <summary>终端宽度</summary>
    public int TerminalWidth { get; set; } = 80; // 默认值，运行时更新

    /// <summary>最小终端高度要求</summary>
    public const int MinTerminalHeight = 10;

    /// <summary>是否需要刷新</summary>
    public bool NeedsRefresh { get; set; } = true;

    /// <summary>当前屏幕</summary>
    public string CurrentScreen { get; set; } = "main";

    // ========== 消息导航状态 ==========

    /// <summary>搜索关键词</summary>
    public string? SearchKeyword { get; set; }

    /// <summary>搜索匹配的消息索引列表</summary>
    public List<int> SearchMatchIndices { get; } = new();

    /// <summary>当前搜索匹配索引（用于导航）</summary>
    public int CurrentSearchMatchIndex { get; set; } = 0;

    /// <summary>是否正在搜索模式</summary>
    public bool IsSearchMode { get; set; } = false;

    /// <summary>折叠的消息ID集合（使用消息时间戳作为标识）</summary>
    public HashSet<string> FoldedMessageIds { get; } = new();

    /// <summary>当前高亮的导航消息索引</summary>
    public int HighlightedMessageIndex { get; set; } = -1;

    // ========== 向后兼容的属性代理 ==========

    // 工作区属性代理
    public string WorkspaceRoot { get => Agent.WorkspaceRoot; set => Agent.WorkspaceRoot = value; }
    public string CurrentAgentKey { get => Agent.CurrentAgentKey; set => Agent.CurrentAgentKey = value; }
    public string SessionId => Agent.SessionId;

    // 配置属性代理
    public string? CurrentModel { get => Agent.CurrentModel; set => Agent.CurrentModel = value; }
    public string RulesMarkdown { get => Agent.RulesMarkdown; set => Agent.RulesMarkdown = value; }
    public IReadOnlyList<string> RulesSources { get => Agent.RulesSources; set => Agent.RulesSources = value; }

    // 工具状态代理
    public List<string> RegisteredMcpToolIds => Agent.RegisteredMcpToolIds;
    public int ToolCount { get => Agent.ToolCount; set => Agent.ToolCount = value; }
    public int SkillCount { get => Agent.SkillCount; set => Agent.SkillCount = value; }
    public int McpServerCount { get => Agent.McpServerCount; set => Agent.McpServerCount = value; }
    public int ExtensionCount { get => Agent.ExtensionCount; set => Agent.ExtensionCount = value; }

    // 详细信息代理
    public IReadOnlyDictionary<string, SkillInfo> SkillInfos { get => Agent.SkillInfos; set => Agent.SkillInfos = value; }
    public IReadOnlyCollection<ITool> ToolInfos { get => Agent.ToolInfos; set => Agent.ToolInfos = value; }
    public IReadOnlyCollection<string> McpServerNames { get => Agent.McpServerNames; set => Agent.McpServerNames = value; }
    public IReadOnlyCollection<McpTool> McpToolInfos { get => Agent.McpToolInfos; set => Agent.McpToolInfos = value; }

    // 消息属性代理
    public StreamingMessage? CurrentStreamingMessage
    {
        get => Messages.CurrentStreamingMessage;
        set
        {
            if (Messages.CurrentStreamingMessage != value)
            {
                Messages.CurrentStreamingMessage = value;
                OnStateChanged(RenderRegion.Streaming);
            }
        }
    }

    public List<ToolCallDisplay> CurrentToolCalls => Messages.CurrentToolCalls;
    public int MaxMessages { get => Messages.MaxMessages; set => Messages.MaxMessages = value; }

    // ========== 事件 ==========

    public event EventHandler<StateChangedEvent>? StateChanged;
    public void OnStateChanged(RenderRegion region)
    {
        StateChanged?.Invoke(this, new StateChangedEvent(region));
    }

    // ========== 方法 ==========

    /// <summary>添加消息</summary>
    public void AddMessage(MessageDisplay msg)
    {
        Messages.Add(msg);
        NeedsRefresh = true;
        OnStateChanged(RenderRegion.Messages);
    }

    /// <summary>清空消息</summary>
    public void ClearMessages()
    {
        Messages.Clear();
        ScrollOffset = 0;
        NeedsRefresh = true;
    }

    /// <summary>更新终端尺寸</summary>
    public void UpdateTerminalSize()
    {
        try
        {
            // 确保获取的高度不低于最小值，防止 RemoveRange 异常
            TerminalHeight = Math.Max(MinTerminalHeight, Console.WindowHeight);
            TerminalWidth = Math.Max(20, Console.WindowWidth); // 最小宽度20
        }
        catch (System.IO.IOException)
        {
            // 在没有控制台句柄的环境中，使用安全默认值
            TerminalHeight = Math.Max(MinTerminalHeight, 24);
            TerminalWidth = Math.Max(20, 80);
        }
        NeedsRefresh = true;
    }

    /// <summary>取消当前任务</summary>
    public void CancelCurrentTask()
    {
        CurrentTaskCts?.Cancel();
        CurrentTaskCts = null;
        IsProcessing = false;
    }

    // ========== 消息导航方法 ==========

    /// <summary>清空搜索状态</summary>
    public void ClearSearch()
    {
        SearchKeyword = null;
        SearchMatchIndices.Clear();
        CurrentSearchMatchIndex = 0;
        IsSearchMode = false;
        HighlightedMessageIndex = -1;
        NeedsRefresh = true;
        OnStateChanged(RenderRegion.Messages);
    }

    /// <summary>设置搜索关键词并执行搜索</summary>
    public void SetSearchKeyword(string? keyword)
    {
        SearchKeyword = keyword;
        SearchMatchIndices.Clear();
        CurrentSearchMatchIndex = 0;

        if (!string.IsNullOrEmpty(keyword))
        {
            // 搜索匹配的消息索引（不遍历所有消息，使用简单匹配）
            for (var i = 0; i < Messages.Count; i++)
            {
                var msg = Messages[i];
                if (msg.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(msg.Reasoning) && msg.Reasoning.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    SearchMatchIndices.Add(i);
                }
            }

            if (SearchMatchIndices.Count > 0)
            {
                IsSearchMode = true;
                HighlightedMessageIndex = SearchMatchIndices[0];
                // 滚动到第一个匹配项
                ScrollToMatch(0);
            }
            else
            {
                IsSearchMode = false;
                HighlightedMessageIndex = -1;
            }
        }
        else
        {
            IsSearchMode = false;
            HighlightedMessageIndex = -1;
        }

        NeedsRefresh = true;
        OnStateChanged(RenderRegion.Messages);
    }

    /// <summary>导航到下一个搜索匹配项</summary>
    public void NavigateNextMatch()
    {
        if (SearchMatchIndices.Count == 0) return;

        CurrentSearchMatchIndex = (CurrentSearchMatchIndex + 1) % SearchMatchIndices.Count;
        ScrollToMatch(CurrentSearchMatchIndex);
    }

    /// <summary>导航到上一个搜索匹配项</summary>
    public void NavigatePrevMatch()
    {
        if (SearchMatchIndices.Count == 0) return;

        CurrentSearchMatchIndex = (CurrentSearchMatchIndex - 1 + SearchMatchIndices.Count) % SearchMatchIndices.Count;
        ScrollToMatch(CurrentSearchMatchIndex);
    }

    /// <summary>滚动到指定的搜索匹配项</summary>
    private void ScrollToMatch(int matchIndex)
    {
        if (matchIndex >= 0 && matchIndex < SearchMatchIndices.Count)
        {
            HighlightedMessageIndex = SearchMatchIndices[matchIndex];
            // 计算滚动偏移以显示匹配消息
            // 让匹配消息在中间位置显示
            var targetIndex = HighlightedMessageIndex;
            var safeHeight = Math.Max(MinTerminalHeight, TerminalHeight);
            var estimatedVisibleCount = Math.Max(1, Math.Max(10, safeHeight) - 10) / 4;
            var maxOffset = Math.Max(0, Messages.Count - estimatedVisibleCount);

            // 计算偏移：让目标消息在可见区域的中间
            var desiredOffset = Messages.Count - targetIndex - estimatedVisibleCount / 2;
            ScrollOffset = Math.Max(0, Math.Min(desiredOffset, maxOffset));

            NeedsRefresh = true;
            OnStateChanged(RenderRegion.Messages);
        }
    }

    /// <summary>切换消息折叠状态</summary>
    public void ToggleMessageFold(string messageId)
    {
        if (FoldedMessageIds.Contains(messageId))
        {
            FoldedMessageIds.Remove(messageId);
        }
        else
        {
            FoldedMessageIds.Add(messageId);
        }
        NeedsRefresh = true;
        OnStateChanged(RenderRegion.Messages);
    }

    /// <summary>获取消息的唯一标识（使用时间戳）</summary>
    public static string GetMessageId(MessageDisplay msg)
    {
        return msg.Timestamp.ToString("yyyyMMddHHmmssfff");
    }
}

// 顶层事件参数类型及渲染区域枚举已在 TuiRenderTypes.cs 中定义