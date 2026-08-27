using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 子会话（task 工具）事件聚合器 - 每父会话一实例的 IStreamConsumer。
/// <para>
/// 作为父会话流 consumer：识别父流中 toolName=="task" 的 ToolCallEvent，解析 taskId 并订阅子会话事件流。
/// 作为子会话流 consumer：消费子会话 ToolCallEvent 聚合为 SessionTaskStep 写回父 toolCall.TaskSteps
/// （整体替换列表引用），终态（ExecutionComplete/LoopCancelled/ErrorEvent）停止订阅。
/// </para>
/// <para>并发写契约：只写 TaskSteps/Error/TaskId，不碰 Status/Result（Status 以父工具状态为准）。</para>
/// </summary>
public sealed class TaskCardAggregator : IStreamConsumer
{
    private readonly SessionEventStreamRouter _router;
    private readonly ISessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, TaskCardState> _tasks = new();
    private readonly object _persistLock = new();
    private System.Threading.Timer? _persistTimer;
    private bool _dirty;

    public TaskCardAggregator(SessionEventStreamRouter router, ISessionManager sessionManager)
    {
        _router = router;
        _sessionManager = sessionManager;
    }

    public string SessionId => ParentSessionId ?? string.Empty;
    public string? ParentSessionId { get; private set; }

    /// <summary>TaskSteps 变更后触发（携带所属 assistant 消息），页面据此增量同步时间线。</summary>
    public event Action<SessionMessage>? AssistantChanged;

    public void Rebind(string parentSessionId)
    {
        foreach (var (taskId, state) in _tasks.ToArray())
        {
            if (!state.Detached)
                _router.DetachConsumer(taskId, this);
        }
        _tasks.Clear();
        ParentSessionId = parentSessionId;
        _router.AttachConsumer(parentSessionId, this);
    }

    public void Reconcile(string parentSessionId)
    {
        ParentSessionId = parentSessionId;
        var session = _sessionManager.Get(parentSessionId);
        if (session?.Messages == null) return;
        foreach (var msg in session.Messages)
        {
            if (msg.ToolCalls == null) continue;
            foreach (var tc in msg.ToolCalls)
            {
                if (IsTaskToolCall(tc))
                    AttachFromToolCall(tc, msg);
            }
        }
    }

    public void OnEvent(IMessageEvent evt)
    {
        var sessionId = evt.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        if (string.Equals(sessionId, ParentSessionId, StringComparison.Ordinal))
        {
            // 父流：识别 task 工具调用 → 挂载
            if (evt is ToolCallEvent tool && IsTaskToolName(tool.ToolName))
            {
                var (toolCall, owner) = ResolveToolCall(tool);
                if (toolCall != null)
                    AttachFromToolCall(toolCall, owner);
            }
            return;
        }

        // 子流：聚合
        if (_tasks.TryGetValue(sessionId, out var state) && !state.Detached)
        {
            if (ApplyChildEvent(state, evt))
            {
                MarkDirtyAndSchedulePersist();
                AssistantChanged?.Invoke(state.Owner);
            }
            else
            {
                state.Detached = true;
                FlushPersist();
                _router.DetachConsumer(sessionId, this);
            }
        }
    }

    public void OnStreamEnd()
    {
        FlushPersist();
    }

    private void AttachFromToolCall(SessionToolCall toolCall, SessionMessage owner)
    {
        var taskId = ResolveTaskId(toolCall);
        if (string.IsNullOrEmpty(taskId))
        {
            ScheduleRetry(toolCall, owner);
            return;
        }

        // 幂等：同一子会话已挂载（未终态）时跳过
        if (_tasks.TryGetValue(taskId, out var existing) && !existing.Detached)
            return;

        toolCall.TaskId = taskId;
        var state = new TaskCardState(taskId, toolCall, owner);
        _tasks[taskId] = state;
        _router.AttachConsumer(taskId, this, replay: true);
    }

    private string? ResolveTaskId(SessionToolCall toolCall)
    {
        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        if (string.IsNullOrEmpty(ParentSessionId)) return null;

        // 内存缓存枚举
        var children = _sessionManager.ListChildrenAsync(ParentSessionId, SessionKind.SubAgent)
            .GetAwaiter().GetResult();
        var match = children?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        if (match != null) return match.Id;

        // 冷缓存兜底：磁盘枚举
        var diskChildren = _sessionManager.LoadChildrenFromStorageAsync(ParentSessionId)
            .GetAwaiter().GetResult();
        match = diskChildren?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }

    private void ScheduleRetry(SessionToolCall toolCall, SessionMessage owner)
    {
        var parentId = ParentSessionId;
        Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal)) return;
                if (!string.IsNullOrEmpty(toolCall.TaskId)) return;
                if (_tasks.Values.Any(s => ReferenceEquals(s.ToolCall, toolCall))) return;
                var taskId = ResolveTaskId(toolCall);
                if (string.IsNullOrEmpty(taskId)) continue;
                toolCall.TaskId = taskId;
                var state = new TaskCardState(taskId, toolCall, owner);
                _tasks[taskId] = state;
                _router.AttachConsumer(taskId, this, replay: true);
                return;
            }
        });
    }

    private static bool ApplyChildEvent(TaskCardState state, IMessageEvent evt) => evt switch
    {
        ToolCallEvent tool => MergeStep(state.ToolCall, tool),
        ExecutionCompleteEvent => false,          // 终态：结束订阅（不改 Status，由父工具状态为准）
        LoopCancelledEvent => FailStep(state, "子任务已取消", cancelled: true),
        ErrorEvent err => FailStep(state, err.Message, cancelled: false),
        _ => true
    };

    private static bool FailStep(TaskCardState state, string error, bool cancelled)
    {
        if (IsIncomplete(state.ToolCall))
            state.ToolCall.Error = error;
        return false;
    }

    private static bool MergeStep(SessionToolCall toolCall, ToolCallEvent tool)
    {
        var steps = toolCall.TaskSteps?.ToList() ?? new List<SessionTaskStep>();
        var stepKind = tool.Type switch
        {
            MessageEventType.ToolCallPending => "tool_pending",
            MessageEventType.ToolCallRunning => "tool_running",
            MessageEventType.ToolCallComplete => "tool_complete",
            _ => "tool_running"
        };
        var merged = new SessionTaskStep
        {
            StepKind = stepKind,
            ToolCallId = tool.ToolCallId,
            ToolName = tool.ToolName,
            Status = tool.Status.ToString(),
            Preview = Truncate(tool.Output ?? tool.Error),
            Timestamp = tool.Timestamp
        };
        var existing = steps.FirstOrDefault(s => s.ToolCallId == tool.ToolCallId);
        if (existing != null) steps[steps.IndexOf(existing)] = merged;
        else steps.Add(merged);

        // 并发写契约：整体替换列表引用
        toolCall.TaskSteps = steps;
        return true;
    }

    private void MarkDirtyAndSchedulePersist()
    {
        lock (_persistLock)
        {
            _dirty = true;
            _persistTimer ??= new System.Threading.Timer(_ => FlushPersist(), null,
                TimeSpan.FromMilliseconds(1000), System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPersist()
    {
        bool doSave;
        lock (_persistLock)
        {
            if (!_dirty) return;
            _dirty = false;
            doSave = true;
            _persistTimer?.Dispose();
            _persistTimer = null;
        }
        if (doSave && !string.IsNullOrEmpty(ParentSessionId))
            _sessionManager.SaveAsync(ParentSessionId).GetAwaiter().GetResult();
    }

    private (SessionToolCall?, SessionMessage?) ResolveToolCall(ToolCallEvent evt)
    {
        var session = _sessionManager.Get(evt.SessionId);
        if (session?.Messages == null) return (null, null);
        foreach (var msg in session.Messages)
        {
            var tc = msg.ToolCalls?.FirstOrDefault(t => t.Id == evt.ToolCallId);
            if (tc != null) return (tc, msg);
        }
        return (null, null);
    }

    private static bool IsTaskToolCall(SessionToolCall tc) =>
        string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(tc.TaskId);

    private static bool IsTaskToolName(string? name) =>
        string.Equals(name, "task", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncomplete(SessionToolCall tc)
    {
        var status = tc.Status?.ToLowerInvariant();
        return status is null or "pending" or "running";
    }

    private static string? Truncate(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed class TaskCardState
    {
        public string TaskId { get; }
        public SessionToolCall ToolCall { get; }
        public SessionMessage Owner { get; }
        public bool Detached { get; set; }
        public TaskCardState(string taskId, SessionToolCall toolCall, SessionMessage owner)
        { TaskId = taskId; ToolCall = toolCall; Owner = owner; }
    }
}
