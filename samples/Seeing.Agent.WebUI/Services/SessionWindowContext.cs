using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
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
    /// <summary>当前会话在所属组内的父会话 ID（根会话为 null）</summary>
    public string? ParentSessionId { get; internal set; }

    /// <summary>当前会话在所属组内的成员关系（SessionWindow 加载时写入；组缺失时按会话 Kind 兜底）。</summary>
    public SessionRelation Relation { get; internal set; }

    /// <summary>当前会话是否为所属组的主线锚点（SessionWindow 加载时写入）。</summary>
    public bool IsAnchor { get; internal set; }

    /// <summary>当前会话在所属组内的成员标签（如 trim 备份前缀；无则 null）。</summary>
    public string? Label { get; internal set; }

    /// <summary>当前会话若为交接前任，其后继会话标题（用于"已交接 → X"；无则 null）。</summary>
    public string? SuccessorTitle { get; internal set; }

    // ---- 能力标记（由 SessionWindow 按组内成员 Relation 经 ApplyCapabilities 计算） ----
    /// <summary>是否为 Child 关系成员（只读子会话）</summary>
    public bool IsChild { get; internal set; }
    /// <summary>只读视图（Child：禁止提交/编辑）</summary>
    public bool IsReadOnly { get; internal set; }
    /// <summary>允许切换会话级自动批准（Child 禁止）</summary>
    public bool AllowAutoApproveChange { get; internal set; }
    /// <summary>展示场景徽标（Child 隐藏）</summary>
    public bool ShowScenarioBadge { get; internal set; }
    /// <summary>展示子会话徽标与元信息（仅 Child）</summary>
    public bool ShowSubAgentBadge { get; internal set; }
    /// <summary>展示 ACP Mode/Model 输入（Child 隐藏）</summary>
    public bool ShowAcpModeSelector { get; internal set; }
    /// <summary>展示模型选择（Child 隐藏）</summary>
    public bool ShowModelSelector { get; internal set; }
    /// <summary>展示"返回主会话"（有父的 Child）</summary>
    public bool ShowReturnToParent { get; internal set; }
    /// <summary>允许重命名（Child 禁止）</summary>
    public bool CanRename { get; internal set; }
    /// <summary>允许分离为独立会话（仅 Child）</summary>
    public bool CanDetach { get; internal set; }
    /// <summary>允许分支会话（仅非 Child）</summary>
    public bool CanBranch { get; internal set; }
    /// <summary>允许编辑出站绑定（Child 禁止）</summary>
    public bool CanEditOutboundBinding { get; internal set; }
    /// <summary>允许清空会话（Child 禁止）</summary>
    public bool CanClear { get; internal set; }
    /// <summary>允许新建会话（Child 禁止）</summary>
    public bool CanCreateSession { get; internal set; }

    /// <summary>
    /// 按组内成员关系计算窗口能力标记：
    /// Child（<see cref="SessionRelation.Child"/>）为只读子会话——禁改名/清空/出站绑定/自动批准切换，
    /// 隐藏场景徽标与模型/ACP 选择，但可分离为独立会话并在有父时返回父会话。
    /// </summary>
    public void ApplyCapabilities(bool isChild, bool hasParent)
    {
        IsChild = isChild;
        IsReadOnly = isChild;
        AllowAutoApproveChange = !isChild;
        ShowScenarioBadge = !isChild;
        ShowSubAgentBadge = isChild;
        ShowAcpModeSelector = !isChild;
        ShowModelSelector = !isChild;
        ShowReturnToParent = isChild && hasParent;
        CanRename = !isChild;
        CanDetach = isChild;
        CanBranch = !isChild;
        CanEditOutboundBinding = !isChild;
        CanClear = !isChild;
        CanCreateSession = !isChild;
    }

    public bool IsQueued { get; internal set; }
    public int QueuePosition { get; internal set; }
    public bool HasActiveExecution { get; internal set; }
    public bool IsExecuting { get; internal set; }
    public string SelectedAgent { get; internal set; } = string.Empty;
    public string SelectedModel { get; internal set; } = string.Empty;
    public string SelectedThinkingEffort { get; internal set; } = string.Empty;
    public string SelectedAcpMode { get; internal set; } = string.Empty;
    public ExecutionStatus? ExecutionStatus { get; internal set; }
    public TodoListViewModel? CurrentTodoList { get; internal set; }
    /// <summary>当前 Agent 是否 ACP 透传（窗口计算刷新，外层头部据此切换 ACP Mode/Model 输入框）</summary>
    public bool IsAcpPassthrough { get; internal set; }
    /// <summary>Native 模型是否校验失败（发送时校验，外层头部据此标红模型下拉框）</summary>
    public bool ModelInvalid { get; internal set; }

    /// <summary>会话级 Scenario 原始值（null = 跟随进程级）。</summary>
    public string? SessionScenario { get; internal set; }

    /// <summary>有效场景名（session.Scenario ?? 进程级）。</summary>
    public string? EffectiveScenario { get; internal set; }

    // ---- 操作（窗口内实现） ----
    public Func<string, Task>? SetAgentAsync { get; internal set; }
    public Func<string, Task>? SetModelAsync { get; internal set; }
    public Func<string?, Task>? SetThinkingEffortAsync { get; internal set; }
    /// <summary>切换会话级 Scenario（null/空 = 跟随进程）；只影响下一次 Submit。</summary>
    public Func<string?, Task>? SetScenarioAsync { get; internal set; }
    public Action<string>? SetAcpMode { get; internal set; }
    public Func<string, Task>? RenameAsync { get; internal set; }
    public Func<Task>? BranchAsync { get; internal set; }
    public Func<Task>? ClearAsync { get; internal set; }
    public Action? ReturnToParent { get; internal set; }
    public Func<string>? GetWorkspace { get; internal set; }
    /// <summary>保存出站绑定（ChannelId/UserId 已由 UI 规范化，窗口实现负责持久化）</summary>
    public Func<string?, string?, Task>? SaveOutboundAsync { get; internal set; }

    // ---- 事件 ----
    public event Action? StateChanged;
    public event Action? SessionLoaded;

    internal void NotifyStateChanged() => StateChanged?.Invoke();
    internal void NotifySessionLoaded() => SessionLoaded?.Invoke();
}
