using System.Text.Json;
using System.Threading.Channels;
using Seeing.Agent.Abstractions.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// task 子代理进度追踪：解析父流 task 工具调用对应的子会话，订阅子会话事件泵，
/// 把子流 <c>tool.call.*</c> 聚合为父视图态的步骤列表（不回写 <see cref="SessionData"/>）。
/// </summary>
public sealed class TuiTaskTracker : IAsyncDisposable
{
    private const int MaxChildDepth = 8;

    private static readonly string[] s_summaryKeys =
    [
        "description", "command", "query", "path", "file_path", "pattern",
        "url", "prompt", "content", "input", "subagent_type", "name",
    ];

    private readonly TuiViewState _state;
    private readonly Func<string, ITuiEventPump> _pumpFactory;
    private readonly ISessionGroupManager _groups;
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskMount> _mounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskMount> _byCall = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public TuiTaskTracker(TuiViewState state, Func<string, ITuiEventPump> pumpFactory, ISessionGroupManager groups)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _pumpFactory = pumpFactory ?? throw new ArgumentNullException(nameof(pumpFactory));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
    }

    /// <summary>检测 task 工具调用并异步挂载子会话流（同 CallId/同 TaskId 幂等）。</summary>
    public void Observe(TuiToolState tool)
    {
        if (tool is null || _disposed || !IsTaskTool(tool))
            return;

        _ = ObserveAsync(tool);
    }

    /// <summary>会话切换/快照重载后重扫全部 task 工具调用并重新挂载（幂等）。</summary>
    public async Task ReconcileAsync()
    {
        if (_disposed)
            return;

        foreach (var block in _state.Tools.ToArray())
        {
            var tool = block.Tool;
            if (tool is null || !IsTaskTool(tool))
                continue;

            // 已终态的挂载保留标记并短路，绝不重建 pump / 重新订阅（避免回放重复步骤）。
            lock (_lock)
            {
                if (_disposed || IsTerminalMountedLocked(tool))
                    continue;
            }

            await ObserveAsync(tool).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        TaskMount[] mounts;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            mounts = _mounts.Values.ToArray();
            _mounts.Clear();
            _byCall.Clear();
            _resolving.Clear();
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(mounts.Select(m => m.ConsumeTask ?? Task.CompletedTask)).ConfigureAwait(false);
        }
        catch
        {
            // 消费循环内部已吞异常，此处仅兜底
        }

        foreach (var mount in mounts)
            await mount.ReleaseAsync().ConfigureAwait(false);

        _disposeCts.Dispose();
    }

    private async Task ObserveAsync(TuiToolState tool)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_byCall.TryGetValue(tool.CallId, out var byCall))
            {
                if (byCall.Terminal)
                    return;

                byCall.Rebind(tool);
                NotifyChanged(tool);
                return;
            }

            if (!string.IsNullOrEmpty(tool.TaskId) && _mounts.TryGetValue(tool.TaskId, out var byTask))
            {
                _byCall[tool.CallId] = byTask;

                // 已终态：仅保留标记，不重建 pump、不再订阅。
                if (byTask.Terminal)
                    return;

                byTask.Rebind(tool);
                NotifyChanged(tool);
                return;
            }

            if (!_resolving.Add(tool.CallId))
                return;
        }

        try
        {
            var taskId = await ResolveTaskIdAsync(tool).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(taskId))
                await MountAsync(taskId, tool).ConfigureAwait(false);
        }
        catch
        {
            // 挂载失败不阻断父事件流
        }
        finally
        {
            lock (_lock)
                _resolving.Remove(tool.CallId);
        }
    }

    private async Task MountAsync(string taskId, TuiToolState tool)
    {
        TaskMount mount;
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_mounts.TryGetValue(taskId, out var existing))
            {
                _byCall[tool.CallId] = existing;
                tool.TaskId = taskId;

                // 已终态：仅绑定并短路，不重建 pump。
                if (existing.Terminal)
                    return;

                existing.Rebind(tool);
                NotifyChanged(tool);
                return;
            }

            mount = new TaskMount(taskId, tool, _pumpFactory(taskId));
            mount.Rebind(tool);
            _mounts[taskId] = mount;
            _byCall[tool.CallId] = mount;
            tool.TaskId = taskId;
        }

        try
        {
            await mount.Pump.StartAsync(CancellationToken.None).ConfigureAwait(false);
            mount.StartConsume(ConsumeAsync(mount));
        }
        catch
        {
            lock (_lock)
            {
                if (_mounts.TryGetValue(taskId, out var current) && ReferenceEquals(current, mount))
                    _mounts.Remove(taskId);
                if (_byCall.TryGetValue(tool.CallId, out var bound) && ReferenceEquals(bound, mount))
                    _byCall.Remove(tool.CallId);
            }

            await mount.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync(TaskMount mount)
    {
        try
        {
            await foreach (var evt in mount.Pump.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                if (_disposed || IsTerminal(evt))
                    break;

                if (evt is ToolCallEvent toolCall)
                    ApplyStep(mount, toolCall);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止/释放
        }
        catch
        {
            // 子流消费异常隔离
        }
        finally
        {
            lock (_lock)
                mount.Terminal = true;

            await mount.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private void ApplyStep(TaskMount mount, ToolCallEvent evt)
    {
        lock (_lock)
        {
            var tool = mount.Tool;
            var step = new TuiTaskStep(
                evt.ToolName ?? string.Empty,
                BuildSummary(evt),
                TuiViewState.MapStatus(evt.Status.ToString()));

            // 不原地改已发布的 Tool.Steps——构造新集合与新 Tool 对象整体发布，避免渲染线程撕裂读。
            var steps = new List<TuiTaskStep>(tool.Steps);
            if (mount.StepIndex.TryGetValue(evt.ToolCallId, out var index) && index < steps.Count)
            {
                steps[index] = step;
            }
            else
            {
                mount.StepIndex[evt.ToolCallId] = steps.Count;
                steps.Add(step);
            }

            var next = CloneTool(tool);
            next.Steps.Clear();
            next.Steps.AddRange(steps);
            mount.Rebind(next);
            NotifyChanged(next);
        }
    }

    private async Task<string?> ResolveTaskIdAsync(TuiToolState tool)
    {
        if (!string.IsNullOrEmpty(tool.TaskId))
            return tool.TaskId;

        var parent = _state.SessionId;
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(tool.CallId))
            return null;

        return await FindChildByOriginAsync(parent, tool.CallId, 0).ConfigureAwait(false);
    }

    private async Task<string?> FindChildByOriginAsync(string parentId, string callId, int depth)
    {
        if (depth > MaxChildDepth)
            return null;

        IReadOnlyList<SessionData> children;
        try
        {
            children = await _groups.ListChildrenAsync(parentId).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (children is null || children.Count == 0)
            return null;

        foreach (var child in children)
        {
            if (child.Metadata is not null
                && child.Metadata.TryGetValue(SessionMetadataKeys.OriginToolCallId, out var origin)
                && string.Equals(origin, callId, StringComparison.Ordinal))
                return child.Id;
        }

        foreach (var child in children)
        {
            var nested = await FindChildByOriginAsync(child.Id, callId, depth + 1).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(nested))
                return nested;
        }

        return null;
    }

    private void NotifyChanged(TuiToolState tool)
    {
        // 通过 Update 在视图态锁内整体替换 Tool 引用并推进 Revision（不原地改集合）。
        _state.Update($"tool:{tool.CallId}", b =>
        {
            b.Tool = tool;
            b.UpdatedAt = DateTime.Now;
        });
    }

    /// <summary>构造携带全部字段与新 <see cref="TuiToolState.Steps"/> 列表的副本。</summary>
    private static TuiToolState CloneTool(TuiToolState source)
    {
        var clone = new TuiToolState
        {
            CallId = source.CallId,
            Name = source.Name,
            Status = source.Status,
            Arguments = source.Arguments,
            Output = source.Output,
            Error = source.Error,
            Title = source.Title,
            TaskId = source.TaskId,
            TaskAgent = source.TaskAgent,
            TaskDescription = source.TaskDescription,
            IsExpanded = source.IsExpanded,
        };
        clone.Steps.AddRange(source.Steps);
        return clone;
    }

    /// <summary>仅在已持锁时调用：该 task 的挂载是否已终态（保留标记，须短路）。</summary>
    private bool IsTerminalMountedLocked(TuiToolState tool)
    {
        if (_byCall.TryGetValue(tool.CallId, out var byCall) && byCall.Terminal)
            return true;

        return !string.IsNullOrEmpty(tool.TaskId)
            && _mounts.TryGetValue(tool.TaskId, out var byTask)
            && byTask.Terminal;
    }

    private static bool IsTaskTool(TuiToolState tool) =>
        string.Equals(tool.Name, "task", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(tool.TaskId);

    private static bool IsTerminal(IMessageEvent evt) =>
        evt is ExecutionCompleteEvent or LoopCancelledEvent or ErrorEvent;

    private static string BuildSummary(ToolCallEvent evt)
    {
        var fromArgs = SummarizeArguments(evt.Arguments);
        if (!string.IsNullOrWhiteSpace(fromArgs))
            return Truncate(fromArgs);
        if (!string.IsNullOrWhiteSpace(evt.Title))
            return Truncate(evt.Title);
        if (!string.IsNullOrWhiteSpace(evt.Output))
            return Truncate(evt.Output);
        if (!string.IsNullOrWhiteSpace(evt.Error))
            return Truncate(evt.Error);
        return string.Empty;
    }

    private static string? SummarizeArguments(object? arguments) => arguments switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        JsonElement element => SummarizeJsonElement(element),
        IDictionary<string, object> dict => SummarizeDictionary(dict),
        _ => arguments.ToString(),
    };

    private static string? SummarizeDictionary(IDictionary<string, object> dict)
    {
        if (dict.Count == 0)
            return null;

        foreach (var key in s_summaryKeys)
        {
            if (dict.TryGetValue(key, out var value) && ToDisplay(value) is { Length: > 0 } text)
                return text;
        }

        foreach (var value in dict.Values)
        {
            if (ToDisplay(value) is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    private static string? SummarizeJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Object:
                foreach (var key in s_summaryKeys)
                {
                    if (element.TryGetProperty(key, out var property) && ToDisplay(property) is { Length: > 0 } text)
                        return text;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (ToDisplay(property.Value) is { Length: > 0 } text)
                        return text;
                }

                return null;
            case JsonValueKind.Array:
                return element.GetRawText();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return element.GetRawText();
        }
    }

    private static string? ToDisplay(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        },
        bool b => b ? "true" : "false",
        _ => value.ToString(),
    };

    private static string Truncate(string text, int max = 200)
        => text.Length <= max ? text : text[..max] + "…";

    private sealed class TaskMount
    {
        private int _released;

        public TaskMount(string taskId, TuiToolState tool, ITuiEventPump pump)
        {
            TaskId = taskId;
            Tool = tool;
            Pump = pump;
        }

        public string TaskId { get; }
        public TuiToolState Tool { get; private set; }
        public ITuiEventPump Pump { get; }
        public Dictionary<string, int> StepIndex { get; } = new(StringComparer.Ordinal);
        public bool Terminal { get; set; }
        public Task? ConsumeTask { get; private set; }

        public void Rebind(TuiToolState tool) => Tool = tool;

        public void StartConsume(Task task) => ConsumeTask = task;

        public async ValueTask ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try
            {
                await Pump.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 停泵失败不阻断释放
            }

            try
            {
                await Pump.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放失败不阻断
            }
        }
    }
}
