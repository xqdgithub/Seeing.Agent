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
/// <para>
/// 并发安全：多个子流 consume loop 可能并行进入 OnEvent 聚合同一父 toolCall.TaskSteps
/// （读-改-写整体替换），用 _writeLock 串行化 MergeStep/FailStep 与 _tasks 挂载/清理；
/// AssistantChanged 在锁外触发，避免持锁回调。
/// </para>
/// </summary>
public sealed class TaskCardAggregator : IStreamConsumer, IDisposable
{
    private readonly SessionEventStreamRouter _router;
    private readonly ISessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, TaskCardState> _tasks = new();
    private readonly object _persistLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _retrying = new();
    private System.Threading.Timer? _persistTimer;
    private bool _dirty;
    private bool _disposed;

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
        // I2：摘除旧父会话订阅，避免旧父流事件继续聚合/泄漏
        if (!string.IsNullOrEmpty(ParentSessionId)
            && !string.Equals(ParentSessionId, parentSessionId, StringComparison.Ordinal))
            _router.DetachConsumer(ParentSessionId, this);

        // 摘除全部子会话订阅（遍历快照，避免并发 Detach 修改枚举）
        foreach (var (taskId, state) in _tasks.ToArray())
        {
            if (!state.Detached)
                _router.DetachConsumer(taskId, this);
        }

        _writeLock.Wait();
        try
        {
            _tasks.Clear();
        }
        finally
        {
            _writeLock.Release();
        }

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
                    _ = AttachFromToolCallAsync(tc, msg);
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
                if (toolCall != null && owner != null)
                    _ = AttachFromToolCallAsync(toolCall, owner);
            }
            return;
        }

        // 子流：聚合（C1：MergeStep/FailStep 读-改-写整体串行化）
        if (_tasks.TryGetValue(sessionId, out var state))
        {
            var changed = false;
            var terminal = false;
            _writeLock.Wait();
            try
            {
                if (state.Detached)
                    return;
                if (ApplyChildEvent(state, evt))
                {
                    MarkDirtyAndSchedulePersist();
                    changed = true;
                }
                else
                {
                    state.Detached = true;
                    terminal = true;
                }
            }
            finally
            {
                _writeLock.Release();
            }

            if (changed)
            {
                // 锁外触发回调，避免持锁阻塞其他子流
                AssistantChanged?.Invoke(state.Owner);
            }
            else if (terminal)
            {
                _ = FlushPersistAsync();
                _router.DetachConsumer(sessionId, this);
            }
        }
    }

    public void OnStreamEnd()
    {
        _ = FlushPersistAsync();
    }

    private async Task AttachFromToolCallAsync(SessionToolCall toolCall, SessionMessage owner)
    {
        try
        {
            var taskId = await ResolveTaskIdAsync(toolCall);
            if (string.IsNullOrEmpty(taskId))
            {
                ScheduleRetry(toolCall, owner);
                return;
            }

            if (TryMountTask(taskId, toolCall, owner))
                _router.AttachConsumer(taskId, this, replay: true);
        }
        catch
        {
            // 挂载失败不阻断事件流（消费者异常隔离）
        }
    }

    private async Task<string?> ResolveTaskIdAsync(SessionToolCall toolCall)
    {
        if (!string.IsNullOrEmpty(toolCall.TaskId))
            return toolCall.TaskId;

        if (string.IsNullOrEmpty(ParentSessionId)) return null;

        // 内存缓存枚举
        var children = await _sessionManager.ListChildrenAsync(ParentSessionId, SessionKind.SubAgent);
        var match = children?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        if (match != null) return match.Id;

        // 冷缓存兜底：磁盘枚举
        var diskChildren = await _sessionManager.LoadChildrenFromStorageAsync(ParentSessionId);
        match = diskChildren?.FirstOrDefault(c =>
            c.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var oid)
            && string.Equals(oid, toolCall.Id, StringComparison.Ordinal));
        return match?.Id;
    }

    /// <summary>
    /// 原子挂载：幂等 + 按 toolCall 引用防重，返回是否真正挂载（调用方随后在锁外 AttachConsumer）。
    /// </summary>
    private bool TryMountTask(string taskId, SessionToolCall toolCall, SessionMessage owner)
    {
        _writeLock.Wait();
        try
        {
            // 幂等：同一子会话已挂载（未终态）时跳过
            if (_tasks.TryGetValue(taskId, out var existing) && !existing.Detached)
                return false;
            // 防重：同一工具调用已被任意子会话引用时跳过
            if (_tasks.Values.Any(s => ReferenceEquals(s.ToolCall, toolCall)))
                return false;
            toolCall.TaskId = taskId;
            _tasks[taskId] = new TaskCardState(taskId, toolCall, owner);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ScheduleRetry(SessionToolCall toolCall, SessionMessage owner)
    {
        // M4：按 toolCall.Id 防重，避免同一工具调用触发多个重试任务
        if (!_retrying.TryAdd(toolCall.Id, 0))
            return;

        var parentId = ParentSessionId;
        Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(500);
                    if (!string.Equals(ParentSessionId, parentId, StringComparison.Ordinal)) return;
                    if (!string.IsNullOrEmpty(toolCall.TaskId)) return;
                    var taskId = await ResolveTaskIdAsync(toolCall);
                    if (string.IsNullOrEmpty(taskId)) continue;
                    if (TryMountTask(taskId, toolCall, owner))
                        _router.AttachConsumer(taskId, this, replay: true);
                    return;
                }
            }
            finally
            {
                _retrying.TryRemove(toolCall.Id, out _);
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

        // 并发写契约：整体替换列表引用（调用方 _writeLock 串行化）
        toolCall.TaskSteps = steps;
        return true;
    }

    private void MarkDirtyAndSchedulePersist()
    {
        lock (_persistLock)
        {
            _dirty = true;
            _persistTimer ??= new System.Threading.Timer(_ =>
            {
                try
                {
                    _ = FlushPersistAsync();
                }
                catch
                {
                    // 防抖落盘异常不传播到 Timer 回调
                }
            }, null, TimeSpan.FromMilliseconds(1000), System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushPersistAsync()
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
        if (!doSave || string.IsNullOrEmpty(ParentSessionId)) return;

        // I5：落盘串行化，避免 Timer 回调与终态 Flush 并发保存导致旧快照覆盖新快照
        await _saveLock.WaitAsync();
        try
        {
            await _sessionManager.SaveAsync(ParentSessionId);
        }
        finally
        {
            _saveLock.Release();
        }
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // I3：先尽力 flush（防抖窗口内未落盘的 TaskSteps 不丢失），再释放锁与 Timer。
        try
        {
            FlushPersistAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 尽力而为：落盘失败不阻断释放
        }

        // 等待 in-flight flush（Timer 回调 / 终态触发）归还 _saveLock 后再释放，避免释放后的
        // 锁被并发 Release 抛 ObjectDisposedException
        try
        {
            _saveLock.Wait();
            _saveLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // 已被并发路径释放，忽略
        }

        _writeLock.Dispose();
        _saveLock.Dispose();
        _persistTimer?.Dispose();
        _persistTimer = null;
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
