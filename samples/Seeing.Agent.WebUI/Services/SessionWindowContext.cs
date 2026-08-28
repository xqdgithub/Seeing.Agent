using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话窗口显示模式：Full=完整会话视图（头部外置，窗口承载消息列/输入/Todo/压缩）；
/// Summary=摘要卡片（会议大屏等场景仅展示标题与状态）。
/// </summary>
public enum SessionWindowMode
{
    Full,
    Summary
}

/// <summary>
/// SessionWindow 对外暴露的状态与操作（供外层页头部经 Header 模板读写）。
/// 状态为窗口内部自建 SessionState 的快照；操作为窗口内实现委托。
/// </summary>
public sealed class SessionWindowContext
{
    // ---- 只读状态（窗口刷新） ----
    public string SessionId { get; internal set; } = string.Empty;
    public string Title { get; internal set; } = string.Empty;
    public SessionData? CurrentSession { get; internal set; }
    public bool IsSubAgentView { get; internal set; }
    public bool IsQueued { get; internal set; }
    public int QueuePosition { get; internal set; }
    public bool HasActiveExecution { get; internal set; }
    public bool IsExecuting { get; internal set; }
    public string SelectedAgent { get; internal set; } = string.Empty;
    public string SelectedModel { get; internal set; } = string.Empty;
    public string SelectedAcpMode { get; internal set; } = string.Empty;
    public ExecutionStatus? ExecutionStatus { get; internal set; }
    public TodoListViewModel? CurrentTodoList { get; internal set; }
    /// <summary>当前 Agent 是否 ACP 透传（窗口计算刷新，外层头部据此切换 ACP Mode/Model 输入框）</summary>
    public bool IsAcpPassthrough { get; internal set; }
    /// <summary>Native 模型是否校验失败（发送时校验，外层头部据此标红模型下拉框）</summary>
    public bool ModelInvalid { get; internal set; }

    // ---- 操作（窗口内实现） ----
    public Func<string, Task>? SetAgentAsync { get; internal set; }
    public Func<string, Task>? SetModelAsync { get; internal set; }
    public Action<string>? SetAcpMode { get; internal set; }
    public Func<string, Task>? RenameAsync { get; internal set; }
    public Func<Task>? BranchAsync { get; internal set; }
    public Func<Task>? ClearAsync { get; internal set; }
    public Action? ReturnToParent { get; internal set; }
    public Func<string>? GetWorkspace { get; internal set; }

    // ---- 事件 ----
    public event Action? StateChanged;
    public event Action? SessionLoaded;

    internal void NotifyStateChanged() => StateChanged?.Invoke();
    internal void NotifySessionLoaded() => SessionLoaded?.Invoke();
}
