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

        // 压缩真相 = 摘要消息位置：最后一个摘要之前的消息均为已压缩历史（折叠展示）
        var lastSummaryIndex = -1;
        for (var i = deduped.Count - 1; i >= 0; i--)
        {
            if (deduped[i].IsSummary)
            {
                lastSummaryIndex = i;
                break;
            }
        }

        for (var i = 0; i < deduped.Count; i++)
        {
            var msg = deduped[i];
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            vm.IsCompacted = lastSummaryIndex >= 0 && i < lastSummaryIndex;
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
    /// <para>
    /// 同一次 Agent Loop 内 message Id 会随 step 变化（如 <c>{loopId}_step0</c> → <c>_step1</c>）。
    /// 若流式早期 LoopId 尚未写入，会短暂建出 <c>single-{id}</c> turn；随后补上 LoopId 时必须
    /// 认领/合并，否则会出现 Loop #1 + Loop #2，刷新后 ResetFromSession 又变回一个。
    /// </para>
    /// </summary>
    public void SyncAssistantMessage(SessionMessage msg, string sessionId, bool isComplete = false)
    {
        if (msg == null || !string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return;

        // 摘要消息（压缩产物）独立渲染为 Compaction 条目，绝不归入 Loop：
        // 压缩完成后摘要可能是最后一条 assistant，流式 sync（执行 finally 等）若将其加入 Loop
        // 会把摘要错误渲染为助手消息并排到最新位置（刷新后全量重建走 ResolveKind 才正确）
        if (msg.IsSummary)
            return;

        var incoming = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete);
        var item = ResolveAssistantTurnForSync(incoming, createIfMissing: true);
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
        var materialized = messages.ToList();
        // 增量路径同样按摘要位置推导折叠态：压缩后未全量重置时，已展示的摘要前消息需转为折叠展示
        var lastSummaryIndex = -1;
        for (var i = materialized.Count - 1; i >= 0; i--)
        {
            if (materialized[i].IsSummary)
            {
                lastSummaryIndex = i;
                break;
            }
        }

        var knownIds = CollectKnownMessageIds();
        var added = false;
        var indexInSession = -1;

        foreach (var msg in materialized)
        {
            indexInSession++;
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            // Persist an Id when missing so subsequent reconciles / refresh stay stable.
            // (Legacy messages and some create paths omitted Id; Reset still showed them.)
            if (string.IsNullOrEmpty(msg.Id))
                msg.Id = Guid.NewGuid().ToString("N")[..12];

            var id = msg.Id!;
            var isCompacted = lastSummaryIndex >= 0 && indexInSession < lastSummaryIndex;

            if (knownIds.Contains(id))
            {
                // 已知消息：压缩发生（摘要出现/移动）时同步更新折叠态
                var existing = FindItemForMessageId(id);
                if (existing?.Message != null && existing.Message.IsCompacted != isCompacted)
                {
                    existing.Message.IsCompacted = isCompacted;
                    existing.Touch();
                    added = true;
                }
                continue;
            }

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            vm.IsCompacted = isCompacted;
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

    /// <summary>
    /// 在时间线中按消息 Id 查找对应的展示项（含 Loop 组内的消息）。
    /// </summary>
    private TimelineItem? FindItemForMessageId(string id)
    {
        foreach (var item in _items)
        {
            if (string.Equals(item.Message?.Id, id, StringComparison.Ordinal))
                return item;

            if (item.Turn?.Messages == null)
                continue;
            foreach (var m in item.Turn.Messages)
            {
                if (string.Equals(m.Id, id, StringComparison.Ordinal))
                    return item;
            }
        }
        return null;
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

    /// <summary>
    /// Resolve the assistant turn for a streaming/reconcile sync, adopting orphan <c>single-*</c>
    /// turns when LoopId becomes available so step/Id changes stay in one Loop bubble.
    /// </summary>
    private TimelineItem? ResolveAssistantTurnForSync(MessageViewModel incoming, bool createIfMissing)
    {
        var loopId = incoming.LoopId;
        var messageId = incoming.Id ?? string.Empty;

        if (!string.IsNullOrEmpty(loopId))
        {
            var byLoop = FindTurnByKey(loopId);
            if (byLoop != null)
            {
                AbsorbOrphanSinglesInto(byLoop, loopId, messageId);
                return byLoop;
            }

            var orphan = FindOrphanTurnForMessage(messageId);
            if (orphan != null)
                return RekeyTurn(orphan, loopId);

            // 同 Loop 内 step 切换：上一条可能仍挂在未完成的 single-* / 空 LoopId turn
            var open = FindLastOpenAssistantTurn();
            if (open != null &&
                (open.Key.StartsWith("single-", StringComparison.Ordinal)
                 || string.IsNullOrEmpty(open.Turn?.LoopId)
                 || string.Equals(open.Turn?.LoopId, loopId, StringComparison.Ordinal)))
            {
                var adopted = RekeyTurn(open, loopId);
                AbsorbOrphanSinglesInto(adopted, loopId, messageId);
                return adopted;
            }

            return createIfMissing ? CreateAssistantTurn(loopId, incoming) : null;
        }

        // LoopId 尚未到达：每个 message Id 独立 single-*（避免误把无关助手合并）。
        // 待后续带 LoopId 的 sync 再认领/合并。
        if (!string.IsNullOrEmpty(messageId))
        {
            var singleKey = TimelineItem.AssistantKey(null, messageId);
            var bySingle = FindTurnByKey(singleKey);
            if (bySingle != null)
                return bySingle;

            return createIfMissing ? CreateAssistantTurn(null, incoming) : null;
        }

        if (!createIfMissing)
            return null;

        return CreateAssistantTurn(null, incoming);
    }

    private TimelineItem? FindAssistantTurn(
        string? loopId,
        bool createIfMissing,
        MessageViewModel? seedVm = null,
        string? keyHint = null)
    {
        if (seedVm != null)
            return ResolveAssistantTurnForSync(seedVm, createIfMissing);

        TimelineItem? item = null;

        if (!string.IsNullOrEmpty(loopId))
        {
            item = FindTurnByKey(loopId);
        }
        else if (!string.IsNullOrEmpty(keyHint))
        {
            item = FindTurnByKey(keyHint);
        }
        else
        {
            item = _items.LastOrDefault(i => i.Kind == TimelineItemKind.AssistantTurn);
        }

        if (item != null || !createIfMissing)
            return item;

        return CreateAssistantTurn(loopId, seedVm, keyHint);
    }

    private TimelineItem? FindTurnByKey(string key) =>
        _items.LastOrDefault(i =>
            i.Kind == TimelineItemKind.AssistantTurn && i.Key == key);

    private TimelineItem? FindLastOpenAssistantTurn() =>
        _items.LastOrDefault(i =>
            i.Kind == TimelineItemKind.AssistantTurn && i.Turn is { IsComplete: false });

    private TimelineItem? FindOrphanTurnForMessage(string messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return null;

        var singleKey = TimelineItem.AssistantKey(null, messageId);
        var byKey = FindTurnByKey(singleKey);
        if (byKey != null)
            return byKey;

        return _items.LastOrDefault(i =>
            i.Kind == TimelineItemKind.AssistantTurn &&
            i.Key.StartsWith("single-", StringComparison.Ordinal) &&
            i.Turn?.Messages.Any(m => m.Id == messageId) == true);
    }

    /// <summary>
    /// TimelineItem.Key 为 init-only：用同内容新项替换并改到 canonical loop key。
    /// </summary>
    private TimelineItem RekeyTurn(TimelineItem old, string loopId)
    {
        if (old.Key == loopId)
        {
            if (old.Turn != null)
                old.Turn.LoopId = loopId;
            return old;
        }

        var idx = _items.IndexOf(old);
        if (idx < 0)
            return old;

        // 若目标 key 已存在，把 orphan 消息并入后删除 orphan
        var existing = FindTurnByKey(loopId);
        if (existing?.Turn != null && !ReferenceEquals(existing, old))
        {
            if (old.Turn?.Messages != null)
            {
                foreach (var m in old.Turn.Messages)
                {
                    if (existing.Turn.Messages.All(x => x.Id != m.Id))
                        existing.Turn.Messages.Add(m);
                }

                existing.Turn.IsComplete = existing.Turn.Messages.All(m => m.IsComplete);
                existing.Turn.LoopId = loopId;
                existing.Touch();
            }

            _items.RemoveAt(idx);
            RenumberLoopIndexes();
            return existing;
        }

        var rekeyed = new TimelineItem
        {
            Key = loopId,
            Kind = TimelineItemKind.AssistantTurn,
            Turn = old.Turn
        };
        if (rekeyed.Turn != null)
            rekeyed.Turn.LoopId = loopId;

        _items[idx] = rekeyed;
        rekeyed.Touch();
        return rekeyed;
    }

    private void AbsorbOrphanSinglesInto(TimelineItem target, string loopId, string currentMessageId)
    {
        if (target.Turn == null)
            return;

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (ReferenceEquals(item, target))
                continue;
            if (item.Kind != TimelineItemKind.AssistantTurn || item.Turn?.Messages == null)
                continue;
            if (!item.Key.StartsWith("single-", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(item.Turn.LoopId)
                && !string.Equals(item.Turn.LoopId, loopId, StringComparison.Ordinal))
            {
                continue;
            }

            // 仅吸收未完成、或消息已归属此 LoopId、或就是当前正在 sync 的 message
            var shouldAbsorb = !item.Turn.IsComplete
                || item.Turn.Messages.Any(m =>
                    string.Equals(m.LoopId, loopId, StringComparison.Ordinal)
                    || m.Id == currentMessageId);
            if (!shouldAbsorb)
                continue;

            foreach (var m in item.Turn.Messages)
            {
                if (target.Turn.Messages.All(x => x.Id != m.Id))
                    target.Turn.Messages.Add(m);
            }

            _items.RemoveAt(i);
        }

        target.Turn.LoopId = loopId;
        target.Turn.IsComplete = target.Turn.Messages.All(m => m.IsComplete);
        RenumberLoopIndexes();
    }

    private TimelineItem CreateAssistantTurn(
        string? loopId,
        MessageViewModel? seedVm,
        string? keyHint = null)
    {
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
        var item = new TimelineItem
        {
            Key = key,
            Kind = TimelineItemKind.AssistantTurn,
            Turn = turn
        };
        _items.Add(item);
        return item;
    }

    private void RenumberLoopIndexes()
    {
        var index = 0;
        foreach (var item in _items)
        {
            if (item.Kind != TimelineItemKind.AssistantTurn || item.Turn == null)
                continue;
            item.Turn.LoopIndex = ++index;
        }
    }

    /// <summary>
    /// 判断消息是否属于指定会话（优先按强归属 <see cref="SessionMessage.SessionId"/> 精确匹配；
    /// 无 SessionId 时回退按消息 Id 在会话消息集合中匹配）。
    /// <para>
    /// 会话切换后 EventStreamHandler 可能残留上一会话（父会话）的流式消息指针，
    /// 渲染前必须校验消息归属，避免把父会话内容渲染进当前（子）会话时间线。
    /// </para>
    /// </summary>
    public static bool BelongsToSession(
        SessionMessage? message,
        string? sessionId,
        IReadOnlyList<SessionMessage>? sessionMessages)
    {
        if (message == null || sessionMessages == null)
            return false;

        // 优先用强归属字段精确匹配（同 Id 但 SessionId 不同 → 不属于当前会话）
        if (!string.IsNullOrEmpty(message.SessionId))
            return string.Equals(message.SessionId, sessionId, StringComparison.Ordinal);

        // 无 SessionId（旧数据）时回退按 Id 在会话消息集合中匹配
        if (string.IsNullOrEmpty(message.Id))
            return false;

        return sessionMessages.Any(m =>
            string.Equals(m.Id, message.Id, StringComparison.Ordinal));
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
