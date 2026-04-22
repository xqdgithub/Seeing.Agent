using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// TUI 全局状态管理 - 组合 MessageStore、AgentContext、InputState、NavigationState
/// </summary>
public class TuiState
{
    /// <summary>消息存储</summary>
    public MessageStore Messages { get; } = new();

    /// <summary>Agent 运行时上下文</summary>
    public AgentContext Agent { get; } = new();

    /// <summary>输入状态</summary>
    public InputState Input { get; } = new();

    /// <summary>导航状态</summary>
    public NavigationState Navigation { get; } = new();

    /// <summary>最小终端高度要求</summary>
    public const int MinTerminalHeight = 10;

    // ========== UI 状态 ==========

    /// <summary>消息滚动偏移</summary>
    public int ScrollOffset { get; set; }

    /// <summary>终端高度</summary>
    public int TerminalHeight { get; set; } = 24;

    /// <summary>终端宽度</summary>
    public int TerminalWidth { get; set; } = 80;

    /// <summary>是否需要刷新</summary>
    public bool NeedsRefresh { get; set; } = true;

    // ========== 事件 ==========

    /// <summary>状态变更事件</summary>
    public event EventHandler<StateChangedEvent>? StateChanged;

    /// <summary>构造函数 - 连接子模块事件</summary>
    public TuiState()
    {
        Messages.OnStateChanged = OnStateChanged;
        Input.OnStateChanged = OnStateChanged;
        Navigation.OnStateChanged = OnStateChanged;
        Navigation.Messages = Messages;
        Navigation.GetTerminalHeight = () => TerminalHeight;
        Navigation.SetScrollOffset = offset => ScrollOffset = offset;
    }

    /// <summary>触发状态变更</summary>
    public void OnStateChanged(RenderRegion region) =>
        StateChanged?.Invoke(this, new StateChangedEvent(region));

    // ========== 输入状态代理 ==========

    public bool IsProcessing => Input.IsProcessing;
    public bool IsMultilineMode { get => Input.IsMultilineMode; set => Input.IsMultilineMode = value; }
    public CancellationTokenSource? CurrentTaskCts { get => Input.CurrentTaskCts; set => Input.CurrentTaskCts = value; }
    public void CancelCurrentTask() => Input.CancelCurrentTask();

    // ========== 消息操作 ==========

    public void AddMessage(MessageDisplay msg) => Messages.Add(msg);

    public void ClearMessages()
    {
        Messages.Clear();
        ScrollOffset = 0;
        NeedsRefresh = true;
    }

    /// <summary>更新终端尺寸</summary>
    public void UpdateTerminalSize()
    {
        TerminalHeight = Math.Max(MinTerminalHeight, Console.WindowHeight);
        TerminalWidth = Math.Max(20, Console.WindowWidth);
        NeedsRefresh = true;
    }

    // ========== Agent 属性代理 ==========

    public string WorkspaceRoot { get => Agent.WorkspaceRoot; set => Agent.WorkspaceRoot = value; }
    public string SessionId => Agent.SessionId;
    public string? CurrentModel { get => Agent.CurrentModel; set => Agent.CurrentModel = value; }
    public static string GetMessageId(MessageDisplay msg) => NavigationState.GetMessageId(msg);

    // ========== 导航方法代理 ==========

    public void SetSearchKeyword(string? keyword) => Navigation.SetSearchKeyword(keyword);
    public void NavigateNextMatch() => Navigation.NavigateMatch(1);
    public void NavigatePrevMatch() => Navigation.NavigateMatch(-1);
}