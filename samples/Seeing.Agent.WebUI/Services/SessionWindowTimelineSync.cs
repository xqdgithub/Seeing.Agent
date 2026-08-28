using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App.Events;
using Seeing.Agent.Events;
using Seeing.Agent.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话窗口的"事件 → 时间线/状态"纯 C# 分派（从 Session.razor.OnHandlerChanged 提取）。
/// 职责：仅操作 MessageTimelineStore 与窗口状态属性，不触碰 NavigationManager / IMessageService 等副作用对象，
/// 不触发渲染（渲染副作用经 requestRender/applyTitle 回调交由窗口组件执行）。
/// 导航类事件（NavigateEvent、CommandResultEvent.NavigationTarget）由窗口 razor 层单独处理，不进入本类。
/// </summary>
public sealed class SessionWindowTimelineSync
{
    private readonly MessageTimelineStore _timeline;
    private readonly string _sessionId;
    private readonly Action _requestRender;
    private readonly Action<string> _applyTitle;
    private readonly Func<IReadOnlyList<SessionMessage>?> _getMessages;
    private readonly Func<SessionMessage?> _getStreamingMessage;
    private readonly Func<string?> _getLoopId;

    private int _streamCharCount;
    private int _currentCodeBlockDepth;

    /// <summary>标题状态属性（S6：纯类只更新状态，渲染副作用走 applyTitle）</summary>
    public string? Title { get; set; }

    /// <summary>压缩进行中标志（由 Compaction* 事件维护，窗口渲染用）</summary>
    public bool CompactionInProgress { get; set; }

    public string? CompactionProgress { get; set; }

    public string? CompactionReasoning { get; set; }

    public SessionWindowTimelineSync(
        MessageTimelineStore timeline,
        string sessionId,
        Action requestRender,
        Action<string> applyTitle,
        Func<IReadOnlyList<SessionMessage>?> getMessages,
        Func<SessionMessage?> getStreamingMessage,
        Func<string?> getLoopId)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _applyTitle = applyTitle ?? throw new ArgumentNullException(nameof(applyTitle));
        _getMessages = getMessages ?? throw new ArgumentNullException(nameof(getMessages));
        _getStreamingMessage = getStreamingMessage ?? throw new ArgumentNullException(nameof(getStreamingMessage));
        _getLoopId = getLoopId ?? throw new ArgumentNullException(nameof(getLoopId));
    }

    // ---- 时间线基础操作（窗口加载/切换/清空时调用）----

    public void ResetFromSession() => _timeline.ResetFromSession(_getMessages() ?? Array.Empty<SessionMessage>(), _sessionId);

    public void ReconcileAppendFromSession() => _timeline.ReconcileAppendFromSession(_getMessages() ?? Array.Empty<SessionMessage>(), _sessionId);

    public void CompleteTurn() => _timeline.CompleteTurn(_getLoopId());

    public void SyncAssistant(bool isComplete)
    {
        var msg = _getStreamingMessage();
        if (msg == null)
        {
            ReconcileAppendFromSession();
            return;
        }
        _timeline.SyncAssistantMessage(msg, _sessionId, isComplete);
    }

    // ---- 事件分派（窗口 razor 层在 OnHandlerChanged 桥接中调用）----

    public void ProcessEvent(IMessageEvent evt)
    {
        if (evt == null)
            return;

        // 会话归属过滤：只处理当前绑定会话的事件
        if (!string.IsNullOrEmpty(evt.SessionId)
            && !string.Equals(evt.SessionId, _sessionId, StringComparison.Ordinal))
            return;

        switch (evt)
        {
            case ExecutionCompleteEvent completeEvt:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                ReconcileAppendFromSession();
                break;
            case LoopCompleteEvent:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                break;
            case LoopCancelledEvent:
            case ErrorEvent:
                SyncAssistant(isComplete: true);
                CompleteTurn();
                ReconcileAppendFromSession();
                break;
            case StreamDeltaEvent deltaEvt:
                HandleStreamDelta(deltaEvt);
                break;
            case StreamStartEvent:
            case ToolCallEvent:
            case StreamCompleteEvent:
            case LoopStartEvent:
            case PermissionRequestEvent:
            case PermissionResponseEvent:
            case TodoUpdateEvent:
            case ModeUpdateEvent:
                SyncAssistant(isComplete: false);
                break;
            case SessionTitleChangedEvent titleEvt:
                if (string.Equals(_sessionId, titleEvt.SessionId, StringComparison.Ordinal))
                {
                    Title = titleEvt.Title;
                    _applyTitle(titleEvt.Title);
                }
                break;
            case SessionUpdatedEvent:
                ResetFromSession();
                break;
            case CommandResultEvent commandEvt:
                if (commandEvt.NeedsRefresh)
                {
                    ResetFromSession();
                    ReconcileAppendFromSession();
                }
                // NavigationTarget 导航由窗口 razor 层处理
                break;
            case CompactionStartedEvent:
                CompactionInProgress = true;
                CompactionProgress = string.Empty;
                CompactionReasoning = string.Empty;
                break;
            case CompactionDeltaEvent cde:
                if (!string.IsNullOrEmpty(cde.ContentDelta))
                    CompactionProgress += cde.ContentDelta;
                if (!string.IsNullOrEmpty(cde.ReasoningDelta))
                    CompactionReasoning += cde.ReasoningDelta;
                break;
            case CompactionCompletedEvent:
                CompactionInProgress = false;
                CompactionProgress = null;
                CompactionReasoning = null;
                ResetFromSession();
                ReconcileAppendFromSession();
                break;
            case CompactionFailedEvent cfe:
                CompactionInProgress = false;
                CompactionProgress = null;
                CompactionReasoning = null;
                ResetFromSession();
                ReconcileAppendFromSession();
                break;
            default:
                SyncAssistant(isComplete: false);
                ReconcileAppendFromSession();
                break;
        }

        _requestRender();
    }

    private void HandleStreamDelta(StreamDeltaEvent deltaEvt)
    {
        var contentLen = deltaEvt.ContentDelta?.Length ?? 0;
        var reasoningLen = deltaEvt.ReasoningDelta?.Length ?? 0;
        _streamCharCount += contentLen + reasoningLen;

        var inCodeBlock = _currentCodeBlockDepth > 0;
        var hasNewline = deltaEvt.ContentDelta?.Contains("\n") == true;
        var threshold = inCodeBlock ? 20 : 8;

        if (deltaEvt.ContentDelta?.Contains("```") == true)
        {
            _currentCodeBlockDepth += deltaEvt.ContentDelta.Count(c => c == '`') / 3;
            _currentCodeBlockDepth = Math.Max(0, _currentCodeBlockDepth % 2 == 0 ? 0 : 1);
            threshold = 0;
        }

        if (_streamCharCount >= threshold || hasNewline || threshold == 0)
        {
            SyncAssistant(isComplete: false);
            _streamCharCount = 0;
        }
    }
}
