using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Session-scoped timeline of chat items (user / assistant turns / system / special kinds).
/// </summary>
public sealed class MessageTimelineStore
{
    private readonly List<TimelineItem> _items = new();

    public IReadOnlyList<TimelineItem> Items => _items;

    /// <summary>
    /// Incremented on each <see cref="ResetFromSession"/> so views can detect structural rebuilds
    /// even when the first item Key happens to stay the same.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>Tail identity for cheap dirty checks (null when empty).</summary>
    public string? TailKey { get; private set; }

    /// <summary>Tail <see cref="TimelineItem.Revision"/> (0 when empty).</summary>
    public int TailRevision { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Raised when the UI must force pin to bottom (submit / enter session / etc.).
    /// </summary>
    public event Action? PinRequested;

    public void RequestPinToBottom() => PinRequested?.Invoke();

    public void ResetFromSession(IEnumerable<SessionMessage> messages, string sessionId)
    {
        _items.Clear();

        // Dedupe by Id keeping last occurrence (later wins).
        var deduped = new List<SessionMessage>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            var id = msg.Id;
            if (!string.IsNullOrEmpty(id) && indexById.TryGetValue(id, out var existing))
            {
                deduped[existing] = msg;
                continue;
            }

            if (!string.IsNullOrEmpty(id))
                indexById[id] = deduped.Count;
            deduped.Add(msg);
        }

        var loopIndex = 0;
        Dictionary<string, TimelineItem>? assistantByKey = null;

        foreach (var msg in deduped)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            var kind = ResolveKind(vm);

            if (kind == TimelineItemKind.AssistantTurn)
            {
                var key = TimelineItem.AssistantKey(vm.LoopId, vm.Id);
                assistantByKey ??= new Dictionary<string, TimelineItem>(StringComparer.Ordinal);

                if (assistantByKey.TryGetValue(key, out var existingTurn))
                {
                    existingTurn.Turn!.Messages.Add(vm);
                    existingTurn.Turn.IsComplete = existingTurn.Turn.Messages.All(m => m.IsComplete);
                    existingTurn.Touch();
                    continue;
                }

                loopIndex++;
                var turn = new LoopGroupViewModel
                {
                    LoopId = vm.LoopId ?? key,
                    LoopIndex = loopIndex,
                    Messages = [vm],
                    IsComplete = vm.IsComplete
                };
                var item = new TimelineItem
                {
                    Key = key,
                    Kind = TimelineItemKind.AssistantTurn,
                    Turn = turn
                };
                assistantByKey[key] = item;
                _items.Add(item);
                continue;
            }

            _items.Add(new TimelineItem
            {
                Key = vm.Id,
                Kind = kind,
                Message = vm
            });
        }

        Generation++;
        RefreshTailHint();
        RaiseChanged();
    }

    public void CompleteTurn(string? loopId)
    {
        var item = FindAssistantTurn(loopId, createIfMissing: false);
        if (item?.Turn == null)
            return;

        item.Turn.IsComplete = true;
        foreach (var msg in item.Turn.Messages)
        {
            msg.IsComplete = true;
            if (!string.IsNullOrEmpty(msg.Reasoning))
                msg.IsReasoningComplete = true;
        }

        item.Touch();
        RefreshTailHint();
        RaiseChanged();
    }

    /// <summary>
    /// In-place sync of one assistant SessionMessage into its turn (preserves ToolCall IsExpanded).
    /// Skips <see cref="TimelineItem.Touch"/> / <see cref="Changed"/> when nothing visible changed.
    /// </summary>
    public void SyncAssistantMessage(SessionMessage msg, string sessionId, bool isComplete = false)
    {
        if (msg == null || !string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return;

        var incoming = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete);
        var key = TimelineItem.AssistantKey(incoming.LoopId, incoming.Id);
        var item = FindAssistantTurn(incoming.LoopId, createIfMissing: true, seedVm: incoming, keyHint: key);
        if (item?.Turn == null)
            return;

        var list = item.Turn.Messages;
        var idx = list.FindIndex(m => m.Id == incoming.Id);
        if (idx < 0)
        {
            var byStep = list.FindIndex(m => m.Step == incoming.Step && !m.IsComplete);
            if (byStep >= 0)
            {
                idx = byStep;
            }
            else
            {
                list.Add(incoming);
                item.Turn.IsComplete = list.All(m => m.IsComplete);
                item.Touch();
                RefreshTailHint();
                RaiseChanged();
                return;
            }
        }

        var existing = list[idx];
        if (ReferenceEquals(existing, incoming))
        {
            var seedComplete = list.All(m => m.IsComplete);
            if (item.Turn.IsComplete != seedComplete)
            {
                item.Turn.IsComplete = seedComplete;
                item.Touch();
                RefreshTailHint();
                RaiseChanged();
            }
            return;
        }

        var changed =
            !string.Equals(existing.Content, incoming.Content, StringComparison.Ordinal)
            || !string.Equals(existing.Reasoning, incoming.Reasoning, StringComparison.Ordinal)
            || existing.IsComplete != incoming.IsComplete
            || existing.Step != incoming.Step
            || !string.Equals(existing.LoopId, incoming.LoopId, StringComparison.Ordinal)
            || PartsVisiblyChanged(existing.Parts, incoming.Parts)
            || ToolsVisiblyChanged(existing.ToolCalls, incoming.ToolCalls);

        var nextReasoningComplete = existing.IsReasoningComplete || incoming.IsReasoningComplete;
        if (existing.IsReasoningComplete != nextReasoningComplete)
            changed = true;

        existing.Content = incoming.Content;
        existing.Reasoning = incoming.Reasoning;
        existing.IsComplete = incoming.IsComplete;
        // Latch: once reasoning is complete, never regress while the same message streams.
        existing.IsReasoningComplete = nextReasoningComplete;
        existing.Parts = incoming.Parts;
        existing.Step = incoming.Step;
        existing.LoopId = incoming.LoopId;
        foreach (var tc in incoming.ToolCalls)
            MessageViewModelFactory.MergeToolCall(existing, tc);

        list[idx] = existing;
        var turnComplete = list.All(m => m.IsComplete);
        if (item.Turn.IsComplete != turnComplete)
        {
            item.Turn.IsComplete = turnComplete;
            changed = true;
        }

        if (!changed)
            return;

        item.Touch();
        RefreshTailHint();
        RaiseChanged();
    }

    private static bool PartsVisiblyChanged(
        IReadOnlyList<ContentPartViewModel> existing,
        IReadOnlyList<ContentPartViewModel> incoming)
    {
        if (existing.Count != incoming.Count)
            return true;

        for (var i = 0; i < existing.Count; i++)
        {
            var a = existing[i];
            var b = incoming[i];
            if (!string.Equals(a.Type, b.Type, StringComparison.Ordinal)
                || !string.Equals(a.Url, b.Url, StringComparison.Ordinal)
                || !string.Equals(a.FileName, b.FileName, StringComparison.Ordinal)
                || !string.Equals(a.MimeType, b.MimeType, StringComparison.Ordinal)
                || !string.Equals(a.Text, b.Text, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ToolsVisiblyChanged(
        IReadOnlyList<ToolCallViewModel> existing,
        IReadOnlyList<ToolCallViewModel> incoming)
    {
        if (existing.Count != incoming.Count)
            return true;

        foreach (var tc in incoming)
        {
            var cur = existing.FirstOrDefault(t => t.Id == tc.Id);
            if (cur == null)
                return true;
            if (!string.Equals(cur.Status, tc.Status, StringComparison.Ordinal)
                || !string.Equals(cur.Result, tc.Result, StringComparison.Ordinal)
                || !string.Equals(cur.Error, tc.Error, StringComparison.Ordinal)
                || !string.Equals(cur.Parameters, tc.Parameters, StringComparison.Ordinal)
                || !string.Equals(cur.Name, tc.Name, StringComparison.Ordinal)
                || !string.Equals(cur.TaskId, tc.TaskId, StringComparison.Ordinal)
                || !string.Equals(cur.TaskAgent, tc.TaskAgent, StringComparison.Ordinal)
                || !string.Equals(cur.TaskDescription, tc.TaskDescription, StringComparison.Ordinal)
                || cur.TaskBackground != tc.TaskBackground
                || TaskStepsVisiblyChanged(cur.TaskSteps, tc.TaskSteps)
                || TodoListVisiblyChanged(cur.TodoList, tc.TodoList))
                return true;
        }

        return false;
    }

    private static bool TaskStepsVisiblyChanged(
        IReadOnlyList<SessionTaskStep> existing,
        IReadOnlyList<SessionTaskStep> incoming)
    {
        if (existing.Count != incoming.Count)
            return true;

        for (var i = 0; i < existing.Count; i++)
        {
            var a = existing[i];
            var b = incoming[i];
            if (!string.Equals(a.ToolCallId, b.ToolCallId, StringComparison.Ordinal)
                || !string.Equals(a.Status, b.Status, StringComparison.Ordinal)
                || !string.Equals(a.ToolName, b.ToolName, StringComparison.Ordinal)
                || !string.Equals(a.Preview, b.Preview, StringComparison.Ordinal)
                || !string.Equals(a.StepKind, b.StepKind, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TodoListVisiblyChanged(TodoListViewModel? existing, TodoListViewModel? incoming)
    {
        if (ReferenceEquals(existing, incoming))
            return false;
        if (existing is null || incoming is null)
            return existing is not null || incoming is not null;

        if (existing.Items.Count != incoming.Items.Count)
            return true;

        for (var i = 0; i < existing.Items.Count; i++)
        {
            var a = existing.Items[i];
            var b = incoming.Items[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || !string.Equals(a.Content, b.Content, StringComparison.Ordinal)
                || a.Status != b.Status)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Append session messages that are not yet represented in the timeline (no full rebuild).
    /// Used after submit / execution end for user or system rows added outside the streaming path.
    /// </summary>
    public void ReconcileAppendFromSession(IEnumerable<SessionMessage> messages, string sessionId)
    {
        var knownIds = CollectKnownMessageIds();
        var added = false;

        foreach (var msg in messages)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            // Persist an Id when missing so subsequent reconciles / refresh stay stable.
            // (Legacy messages and some create paths omitted Id; Reset still showed them.)
            if (string.IsNullOrEmpty(msg.Id))
                msg.Id = Guid.NewGuid().ToString("N")[..12];

            var id = msg.Id!;
            if (knownIds.Contains(id))
                continue;

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            var kind = ResolveKind(vm);

            if (kind == TimelineItemKind.AssistantTurn)
            {
                SyncAssistantMessage(msg, sessionId, isComplete: true);
                knownIds.Add(id);
                added = true;
                continue;
            }

            _items.Add(new TimelineItem
            {
                Key = vm.Id,
                Kind = kind,
                Message = vm
            });
            knownIds.Add(id);
            added = true;
        }

        if (added)
        {
            RefreshTailHint();
            RaiseChanged();
        }
    }

    private void RefreshTailHint()
    {
        if (_items.Count == 0)
        {
            TailKey = null;
            TailRevision = 0;
            return;
        }

        var tail = _items[^1];
        TailKey = tail.Key;
        TailRevision = tail.Revision;
    }

    private HashSet<string> CollectKnownMessageIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            if (!string.IsNullOrEmpty(item.Message?.Id))
                ids.Add(item.Message.Id);

            if (item.Turn?.Messages == null)
                continue;

            foreach (var m in item.Turn.Messages)
            {
                if (!string.IsNullOrEmpty(m.Id))
                    ids.Add(m.Id);
            }
        }

        return ids;
    }

    private TimelineItem? FindAssistantTurn(
        string? loopId,
        bool createIfMissing,
        MessageViewModel? seedVm = null,
        string? keyHint = null)
    {
        TimelineItem? item = null;

        if (!string.IsNullOrEmpty(loopId))
        {
            item = _items.LastOrDefault(i =>
                i.Kind == TimelineItemKind.AssistantTurn && i.Key == loopId);
        }
        else if (!string.IsNullOrEmpty(keyHint))
        {
            // keyHint provided: match exactly — never fall back to last turn
            // (would merge distinct assistants that lack loopId).
            item = _items.LastOrDefault(i =>
                i.Kind == TimelineItemKind.AssistantTurn && i.Key == keyHint);
        }
        else
        {
            // CompleteTurn(null) / no identity: attach to the latest assistant turn.
            item = _items.LastOrDefault(i => i.Kind == TimelineItemKind.AssistantTurn);
        }

        if (item != null || !createIfMissing)
            return item;

        var key = keyHint
            ?? TimelineItem.AssistantKey(loopId, seedVm?.Id ?? Guid.NewGuid().ToString("N")[..8]);
        var loopIndex = _items.Count(i => i.Kind == TimelineItemKind.AssistantTurn) + 1;
        var turn = new LoopGroupViewModel
        {
            LoopId = loopId ?? key,
            LoopIndex = loopIndex,
            Messages = seedVm != null ? [seedVm] : [],
            IsComplete = false
        };
        item = new TimelineItem
        {
            Key = key,
            Kind = TimelineItemKind.AssistantTurn,
            Turn = turn
        };
        _items.Add(item);
        return item;
    }

    private static TimelineItemKind ResolveKind(MessageViewModel vm)
    {
        if (vm.IsSystemReminder)
            return TimelineItemKind.Reminder;
        if (vm.IsProjectInstructions)
            return TimelineItemKind.ProjectInstructions;
        if (vm.IsCompactionSummary)
            return TimelineItemKind.Compaction;

        return vm.Role?.ToLowerInvariant() switch
        {
            "system" => TimelineItemKind.System,
            "user" => TimelineItemKind.User,
            "assistant" => TimelineItemKind.AssistantTurn,
            _ => TimelineItemKind.User
        };
    }

    private void RaiseChanged() => Changed?.Invoke();
}
