using System.Text.Json;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Core.Events;
using Seeing.Agent.TokenBudget;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 事件解释器：把 <see cref="IMessageEvent"/> 增量投影到 <see cref="TuiViewState"/>（纯逻辑）。
/// <para>按 Key 幂等 upsert，支持 <c>SubscribeEvents</c> 环形缓冲回放去重；未识别事件返回 false，不抛。</para>
/// </summary>
public sealed class TuiEventInterpreter
{
    private const string TaskToolName = "task";
    private const string TaskIdMetadataKey = "task_id";
    private const string TaskIdPrefix = "task_id:";

    private readonly TuiViewState _state;
    private readonly HashSet<(string? LoopId, int Step)> _seenStreamStart = [];
    private readonly Dictionary<string, int> _currentStepByLoop = new(StringComparer.Ordinal);

    public TuiEventInterpreter(TuiViewState state)
    {
        _state = state;
    }

    /// <summary>当前是否处于执行态（来自具体 <c>ExecutionStarted/CompleteEvent</c> 的配对）。</summary>
    public bool IsExecuting => _state.IsExecuting;

    /// <summary>应用一条事件；返回是否引起视图态变化。</summary>
    public bool Apply(IMessageEvent evt) => evt switch
    {
        ExecutionStartedEvent e => ApplyExecutionStarted(e),
        ExecutionCompleteEvent e => ApplyExecutionComplete(e),
        LoopCompleteEvent e => ApplyLoopComplete(e),
        StreamStartEvent e => ApplyStreamStart(e),
        StreamDeltaEvent e => ApplyStreamDelta(e),
        StreamCompleteEvent e => ApplyStreamComplete(e),
        ToolCallEvent e => ApplyToolCall(e),
        TodoUpdateEvent e => ApplyTodoUpdate(e),
        LoopCancelledEvent e => ApplyLoopCancelled(e),
        ErrorEvent e => ApplyError(e),
        LlmRetryScheduledEvent e => ApplyLlmRetry(e),
        CommandResultEvent e => ApplyCommandResult(e),
        ModeUpdateEvent e => ApplyModeUpdate(e),
        SessionTitleChangedEvent e => ApplySessionTitleChanged(e),
        SessionUpdatedEvent e => ApplySessionUpdated(e),
        BudgetStatusEvent e => ApplyBudgetStatus(e),
        BudgetWarningEvent e => ApplyBudgetWarning(e),
        CompactionStartedEvent e => ApplyCompactionStarted(e),
        CompactionDeltaEvent e => ApplyCompactionDelta(e),
        CompactionCompletedEvent e => ApplyCompactionCompleted(e),
        CompactionFailedEvent e => ApplyCompactionFailed(e),
        _ => false,
    };

    private bool ApplyExecutionStarted(ExecutionStartedEvent e)
    {
        _state.ActiveExecutionId = e.ExecutionId;
        _state.IsExecuting = true;
        _state.Touch();
        return true;
    }

    private bool ApplyExecutionComplete(ExecutionCompleteEvent e)
    {
        if (!string.Equals(_state.ActiveExecutionId, e.ExecutionId, StringComparison.Ordinal))
            return false;

        _state.ActiveExecutionId = null;
        _state.IsExecuting = false;
        _state.Touch();
        return true;
    }

    private bool ApplyLoopComplete(LoopCompleteEvent e)
    {
        var changed = false;
        foreach (var block in _state.Blocks)
        {
            if (!string.Equals(block.LoopId, e.LoopId, StringComparison.Ordinal))
                continue;
            if (!block.IsTerminal)
            {
                block.IsTerminal = true;
                changed = true;
            }
            if (block.IsStreaming)
            {
                block.IsStreaming = false;
                changed = true;
            }
        }

        if (changed)
            _state.Touch();
        return changed;
    }

    private bool ApplyStreamStart(StreamStartEvent e)
    {
        _seenStreamStart.Add((e.LoopId, e.Step));
        _currentStepByLoop[LoopKey(e.LoopId)] = e.Step;

        var key = AssistantKey(e.LoopId, e.Step, null);
        var existing = _state.Find(key);
        if (existing is not null)
        {
            existing.Text = string.Empty;
            existing.Reasoning = string.Empty;
            existing.IsStreaming = true;
            existing.IsTerminal = false;
            existing.UpdatedAt = DateTime.Now;
            _state.Touch();
            return true;
        }

        _state.Upsert(new TuiBlock
        {
            Key = key,
            Kind = TuiBlockKind.Assistant,
            LoopId = e.LoopId,
            Step = e.Step,
            IsStreaming = true,
        });
        return true;
    }

    private bool ApplyStreamDelta(StreamDeltaEvent e)
    {
        var step = ResolveStep(e.LoopId);
        if (!_seenStreamStart.Contains((e.LoopId, step)))
            return false;

        var key = AssistantKey(e.LoopId, step, null);
        var existing = _state.Find(key);
        var text = (existing?.Text ?? string.Empty) + (e.ContentDelta ?? string.Empty);
        var reasoning = (existing?.Reasoning ?? string.Empty) + (e.ReasoningDelta ?? string.Empty);

        _state.Upsert(new TuiBlock
        {
            Key = key,
            Kind = TuiBlockKind.Assistant,
            LoopId = e.LoopId,
            Step = step,
            Text = text,
            Reasoning = reasoning,
            IsStreaming = true,
        });
        return true;
    }

    private bool ApplyStreamComplete(StreamCompleteEvent e)
    {
        var step = ResolveStep(e.LoopId);
        _currentStepByLoop[LoopKey(e.LoopId)] = step;

        _state.Upsert(new TuiBlock
        {
            Key = AssistantKey(e.LoopId, step, null),
            Kind = TuiBlockKind.Assistant,
            LoopId = e.LoopId,
            Step = step,
            Text = e.Message.Content ?? string.Empty,
            Reasoning = e.Message.ReasoningContent ?? string.Empty,
            IsStreaming = false,
            IsTerminal = true,
        });
        return true;
    }

    private bool ApplyToolCall(ToolCallEvent e)
    {
        var key = $"tool:{e.ToolCallId}";
        var block = _state.Find(key);
        var tool = block?.Tool ?? new TuiToolState { CallId = e.ToolCallId, Name = e.ToolName };

        tool.Status = MapStatus(e.Status);
        if (e.Arguments is not null)
            tool.Arguments = FormatArguments(e.Arguments);
        if (e.Output is not null)
            tool.Output = e.Output;
        if (e.Title is not null)
            tool.Title = e.Title;
        if (e.Error is not null)
            tool.Error = e.Error;

        var taskId = ResolveTaskId(e);
        if (taskId is not null)
            tool.TaskId = taskId;
        tool.TaskAgent = ReadMetadataString(e, "task_agent") ?? tool.TaskAgent;
        tool.TaskDescription = ReadMetadataString(e, "task_description") ?? tool.TaskDescription;

        _state.Upsert(new TuiBlock
        {
            Key = key,
            Kind = TuiBlockKind.Tool,
            Title = e.Title ?? block?.Title,
            Tool = tool,
            IsTerminal = tool.Status is not (TuiToolStatus.Pending or TuiToolStatus.Running),
        });
        return true;
    }

    private bool ApplyTodoUpdate(TodoUpdateEvent e)
    {
        _state.Todos = e.Todos
            .Select(t => new TuiTodo(t.Content, MapTodoStatus(t.Status), null))
            .ToList();
        _state.Touch();
        return true;
    }

    private bool ApplyLoopCancelled(LoopCancelledEvent e)
    {
        var changed = false;

        if (_state.IsExecuting)
        {
            _state.IsExecuting = false;
            changed = true;
        }
        if (_state.ActiveExecutionId is not null)
        {
            _state.ActiveExecutionId = null;
            changed = true;
        }

        foreach (var block in _state.Blocks)
        {
            if (!string.Equals(block.LoopId, e.LoopId, StringComparison.Ordinal))
                continue;
            if (!block.IsCancelled)
            {
                block.IsCancelled = true;
                changed = true;
            }
            if (block.IsStreaming)
            {
                block.IsStreaming = false;
                changed = true;
            }
            if (!block.IsTerminal)
            {
                block.IsTerminal = true;
                changed = true;
            }
        }

        if (changed)
            _state.Touch();
        return changed;
    }

    private bool ApplyError(ErrorEvent e)
    {
        _state.LastError = e.Message;
        _state.Upsert(new TuiBlock
        {
            Key = $"error:{LoopKey(e.LoopId)}:{e.Timestamp.Ticks}",
            Kind = TuiBlockKind.Error,
            LoopId = e.LoopId,
            Text = e.Message,
            IsTerminal = true,
        });
        return true;
    }

    private bool ApplyLlmRetry(LlmRetryScheduledEvent e)
    {
        var text = $"重试中 ({e.Attempt})";
        if (e.NextDelayMs > 0)
            text += $" · {e.NextDelayMs}ms";
        if (!string.IsNullOrWhiteSpace(e.Reason))
            text += $" · {e.Reason}";

        _state.Upsert(new TuiBlock
        {
            Key = $"retry:{LoopKey(e.LoopId)}:{e.Step}",
            Kind = TuiBlockKind.System,
            LoopId = e.LoopId,
            Step = e.Step,
            Text = text,
        });
        return true;
    }

    private bool ApplyCommandResult(CommandResultEvent e)
    {
        var text = !string.IsNullOrWhiteSpace(e.Message)
            ? e.Message!
            : e.Success
                ? $"命令 {e.CommandName} 已执行"
                : $"命令 {e.CommandName} 执行失败";

        _state.Upsert(new TuiBlock
        {
            Key = $"cmd:{e.CommandName}:{e.Timestamp.Ticks}",
            Kind = TuiBlockKind.System,
            LoopId = e.LoopId,
            Text = text,
            IsTerminal = true,
        });

        if (!e.Success)
            _state.LastError = text;
        return true;
    }

    private bool ApplyModeUpdate(ModeUpdateEvent e)
    {
        _state.AcpMode = e.ModeId;
        _state.Touch();
        return true;
    }

    private bool ApplySessionTitleChanged(SessionTitleChangedEvent e)
    {
        if (string.Equals(_state.Title, e.Title, StringComparison.Ordinal))
            return false;

        _state.Title = e.Title;
        _state.Touch();
        return true;
    }

    private bool ApplySessionUpdated(SessionUpdatedEvent e)
    {
        var session = e.Session;
        _state.Title = session.Title;
        _state.AgentId = session.SelectedAgent;
        _state.ModelId = session.SelectedModel;
        _state.ThinkingEffort = session.SelectedThinkingEffort;
        _state.Scenario = session.Scenario;
        _state.AcpMode = session.SelectedAcpMode;
        _state.Touch();
        return true;
    }

    private bool ApplyBudgetStatus(BudgetStatusEvent e)
    {
        _state.Budget = new TuiBudget(
            e.CurrentTokens,
            e.MaxTokens > 0 ? e.MaxTokens : null);
        _state.Touch();
        return true;
    }

    private bool ApplyBudgetWarning(BudgetWarningEvent e)
    {
        _state.Upsert(new TuiBlock
        {
            Key = "budget.warning",
            Kind = TuiBlockKind.System,
            Text = e.Message,
        });
        return true;
    }

    private bool ApplyCompactionStarted(CompactionStartedEvent e)
    {
        _state.Upsert(new TuiBlock
        {
            Key = CompactionKey(e.LoopId),
            Kind = TuiBlockKind.Compaction,
            LoopId = e.LoopId,
            Text = $"压缩开始（{e.Reason}）",
            IsStreaming = true,
        });
        return true;
    }

    private bool ApplyCompactionDelta(CompactionDeltaEvent e)
    {
        var key = CompactionKey(e.LoopId);
        var existing = _state.Find(key);
        _state.Upsert(new TuiBlock
        {
            Key = key,
            Kind = TuiBlockKind.Compaction,
            LoopId = e.LoopId,
            Text = (existing?.Text ?? string.Empty) + (e.ContentDelta ?? string.Empty),
            Reasoning = (existing?.Reasoning ?? string.Empty) + (e.ReasoningDelta ?? string.Empty),
            IsStreaming = true,
        });
        return true;
    }

    private bool ApplyCompactionCompleted(CompactionCompletedEvent e)
    {
        var text = !string.IsNullOrWhiteSpace(e.Summary)
            ? e.Summary!
            : $"压缩完成：{e.TokensBefore} → {e.TokensAfter}（移除 {e.MessagesRemoved} 条）";

        _state.Upsert(new TuiBlock
        {
            Key = CompactionKey(e.LoopId),
            Kind = TuiBlockKind.Compaction,
            LoopId = e.LoopId,
            Text = text,
            IsStreaming = false,
            IsTerminal = true,
        });
        return true;
    }

    private bool ApplyCompactionFailed(CompactionFailedEvent e)
    {
        _state.Upsert(new TuiBlock
        {
            Key = CompactionKey(e.LoopId),
            Kind = TuiBlockKind.Compaction,
            LoopId = e.LoopId,
            Title = "压缩失败",
            Text = e.ErrorMessage ?? "压缩失败",
            IsStreaming = false,
            IsTerminal = true,
        });
        return true;
    }

    private int ResolveStep(string? loopId)
    {
        if (_currentStepByLoop.TryGetValue(LoopKey(loopId), out var step))
            return step;

        for (var i = _state.Blocks.Count - 1; i >= 0; i--)
        {
            var block = _state.Blocks[i];
            if (block.Kind == TuiBlockKind.Assistant &&
                string.Equals(block.LoopId, loopId, StringComparison.Ordinal))
                return block.Step;
        }

        return 0;
    }

    private static string AssistantKey(string? loopId, int step, string? messageId)
        => TuiViewState.AssistantKey(loopId, step, messageId ?? $"step{step}");

    private static string LoopKey(string? loopId) => loopId ?? string.Empty;

    private static string CompactionKey(string? loopId) => $"compaction:{LoopKey(loopId)}";

    private static TuiToolStatus MapStatus(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Pending => TuiToolStatus.Pending,
        ToolCallStatus.Running => TuiToolStatus.Running,
        ToolCallStatus.Success => TuiToolStatus.Success,
        ToolCallStatus.Failed => TuiToolStatus.Failed,
        ToolCallStatus.Rejected => TuiToolStatus.Rejected,
        ToolCallStatus.Cancelled => TuiToolStatus.Cancelled,
        _ => TuiToolStatus.Pending,
    };

    private static string MapTodoStatus(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        TodoStatus.Paused => "paused",
        _ => "pending",
    };

    private static string? FormatArguments(object? arguments) => arguments switch
    {
        null => null,
        string s => s,
        _ => SerializeSafe(arguments),
    };

    private static string SerializeSafe(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string? ResolveTaskId(ToolCallEvent e)
    {
        var fromMetadata = ReadMetadataString(e, TaskIdMetadataKey);
        if (!string.IsNullOrWhiteSpace(fromMetadata))
            return fromMetadata;

        if (!string.Equals(e.ToolName, TaskToolName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(e.Output))
            return null;

        foreach (var line in e.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(TaskIdPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = trimmed[TaskIdPrefix.Length..].Trim();
            if (id.Length > 0)
                return id;
        }

        return null;
    }

    private static string? ReadMetadataString(ToolCallEvent e, string key)
    {
        if (e.Metadata is null || !e.Metadata.TryGetValue(key, out var value) || value is null)
            return null;

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
