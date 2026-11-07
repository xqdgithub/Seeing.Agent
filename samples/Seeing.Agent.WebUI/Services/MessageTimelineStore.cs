using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Session-scoped timeline of chat items (user / assistant turns / system / special kinds).
/// <para>
/// 线程契约：本类是有状态的 UI 投影，<b>只允许在 UI 线程</b>（WebUI 即 Blazor Dispatcher）变更。
/// <see cref="ResetFromSession"/>、<see cref="SyncAssistantMessage"/>、<see cref="CompleteTurn"/>、
/// <see cref="ReconcileAppendFromSession"/> 等入口若由事件流后台线程触发
/// （<c>IStreamConsumer.OnEvent</c>、<c>TaskCardAggregator.AssistantChanged</c> 等），
/// 调用方<b>必须先切回 UI 线程</b>（组件内 <c>InvokeAsync</c>）再调用，
/// 否则会与渲染线程并发修改集合，抛出 "Collection was modified"。
/// </para>
/// <para>
/// 与数据层的分工：共享真相源 <see cref="SessionData.Messages"/> 由 Session 层以锁内快照
/// 保证跨线程读安全（后台职责）；本类是需保留增量/展开态的活投影，无法返回快照，
/// 故线程亲和性由 UI 层负责。
/// </para>
/// </summary>
public sealed class MessageTimelineStore
{
    private readonly List<TimelineItem> _items = new();

    /// <summary>排队中（已入站、尚未被消费）的 user 消息投影；不进主时间线，输入框上方独立展示。</summary>
    private readonly List<MessageViewModel> _pending = new();

    /// <summary>Assistant 轮次索引：turn key（loopId 或 single-*）→ 时间线条目。</summary>
    private readonly Dictionary<string, TimelineItem> _turnByKey = new(StringComparer.Ordinal);

    /// <summary>消息 Id → 所属条目（值为 null 表示排队消息，条目尚未建立）。</summary>
    private readonly Dictionary<string, TimelineItem?> _itemByMessageId = new(StringComparer.Ordinal);

    /// <summary>尚未被认领的 single-* 轮次 key（通常为空，避免每 delta 全表扫描）。</summary>
    private readonly HashSet<string> _pendingOrphanKeys = new(StringComparer.Ordinal);

    public IReadOnlyList<TimelineItem> Items => _items;

    /// <summary>排队中消息（按入站顺序）；被消费后移入 <see cref="Items"/>。</summary>
    public IReadOnlyList<MessageViewModel> PendingMessages => _pending;

    /// <summary>
    /// 结构性重建代次。仅当 <see cref="ResetFromSession"/> 的目标会话**发生变化**时递增。
    /// <para>
    /// 视图（<c>MessageList</c>）据此判断是否重算窗口起点。若每次 Reset 都递增，
    /// 同一会话内的重建（压缩完成等）也会触发窗口重算 + 强制贴底 + 整棵子树重建，
    /// 这是切换卡顿的主要来源之一。
    /// </para>
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>当前时间线绑定的会话 Id；用于判断 <see cref="Generation"/> 是否需要递增。</summary>
    private string? _boundSessionId;

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
        _pending.Clear();
        _turnByKey.Clear();
        _itemByMessageId.Clear();
        _pendingOrphanKeys.Clear();

        // Dedupe by Id keeping last occurrence (later wins).
        var deduped = new List<SessionMessage>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            // 缺 Id 的消息无法稳定标识（@key / 增量对账都依赖它），跳过而非回写：
            // 上层写者（ChatEventTracker / SessionMessage 工厂）保证 Id 非空，
            // 投影层不得修改执行期消息字段（docs/architecture/04 §5.2）。
            if (string.IsNullOrEmpty(msg.Id))
                continue;

            var id = msg.Id;
            if (indexById.TryGetValue(id, out var existing))
            {
                deduped[existing] = msg;
                continue;
            }

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
            // 历史空 system 仅承载 schema_snapshot（旧版 ChatEventTracker），不展示
            if (IsSchemaSnapshotCarrier(msg))
                continue;

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            vm.IsCompacted = lastSummaryIndex >= 0 && i < lastSummaryIndex;

            // 排队中的入站 user 消息不进主时间线，单独投影为待消费列表
            if (vm.IsPendingInbox)
            {
                _pending.Add(vm);
                _itemByMessageId[vm.Id] = null;
                continue;
            }

            var kind = ResolveKind(vm);

            if (kind == TimelineItemKind.AssistantTurn)
            {
                var key = TimelineItem.AssistantKey(vm.LoopId, vm.Id);
                assistantByKey ??= new Dictionary<string, TimelineItem>(StringComparer.Ordinal);

                if (assistantByKey.TryGetValue(key, out var existingTurn))
                {
                    existingTurn.Turn!.Messages.Add(vm);
                    existingTurn.Turn.IsComplete = existingTurn.Turn.Messages.All(m => m.IsComplete);
                    // 轮次内新增消息 → 结构变化
                    existingTurn.TouchStructure();
                    _itemByMessageId[vm.Id] = existingTurn;
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
                _turnByKey[key] = item;
                _itemByMessageId[vm.Id] = item;
                if (key.StartsWith("single-", StringComparison.Ordinal))
                    _pendingOrphanKeys.Add(key);
                continue;
            }

            var plainItem = new TimelineItem
            {
                Key = vm.Id,
                Kind = kind,
                Message = vm
            };
            _items.Add(plainItem);
            _itemByMessageId[vm.Id] = plainItem;
        }

        // 仅当换会话时才递增 Generation：同会话内的重建（压缩完成等）不应触发
        // 视图的窗口重算与整棵子树重建。
        if (!string.Equals(_boundSessionId, sessionId, StringComparison.Ordinal))
        {
            _boundSessionId = sessionId;
            Generation++;
        }

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
            var visibleChanged = false;

            if (!msg.IsComplete)
            {
                msg.IsComplete = true;
                visibleChanged = true;
            }

            if (!string.IsNullOrEmpty(msg.Reasoning) && !msg.IsReasoningComplete)
            {
                msg.IsReasoningComplete = true;
                visibleChanged = true;
            }

            // 消息自身的版本必须递增：LoopGroupItem 的分段缓存按 MessageViewModel.Revision 判定，
            // 不递增会复用流式期的纯文本片段，导致完成后永久停留未格式化状态。
            if (visibleChanged)
                msg.Touch();
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
    /// <param name="msg">会话消息（真相源）。</param>
    /// <param name="sessionId">所属会话 Id。</param>
    /// <param name="isComplete">
    /// 完成态声明。<c>null</c>（默认）= <b>沿用时间线中该消息的既有完成态</b>；
    /// 消息尚未投影时按「未完成」处理。
    /// <para>
    /// <b>不要提供「默认流式」的语义</b>：默认值曾为 <c>false</c>，导致
    /// <c>SessionWindow.OnAggregatorAssistantChanged</c>（子 Agent 每上报一个 TaskStep）
    /// 把父会话里**已完成**的回答重新标成未完成，绕过 <c>SessionWindowTimelineSync</c>
    /// 的全部门闸，Markdown 塌成纯文本，直到刷新页面才恢复。
    /// 只有真正知道消息在流的调用方（流式同步）才可显式传 <c>false</c>。
    /// </para>
    /// </param>
    public void SyncAssistantMessage(SessionMessage msg, string sessionId, bool? isComplete = null)
    {
        if (msg == null || !string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return;

        // 摘要消息（压缩产物）独立渲染为 Compaction 条目，绝不归入 Loop：
        // 压缩完成后摘要可能是最后一条 assistant，流式 sync（执行 finally 等）若将其加入 Loop
        // 会把摘要错误渲染为助手消息并排到最新位置（刷新后全量重建走 ResolveKind 才正确）
        if (msg.IsSummary)
            return;

        // 未声明完成态 → 沿用既有态。必须先查再建 VM：VM 工厂会据此推导 IsReasoningComplete。
        var effectiveComplete = isComplete
            ?? (TryFindMessage(msg.Id, out var current) && current.IsComplete);

        var incoming = MessageViewModelFactory.FromSessionMessage(msg, sessionId, effectiveComplete);
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
                // 轮次内新增了消息 → 结构变化（分段缓存需重建全部分段）
                item.TouchStructure();
                _itemByMessageId[incoming.Id] = item;
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

        // 消息自身的版本必须递增（分段缓存按 MessageViewModel.Revision 判定）
        existing.Touch();
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
                || !string.Equals(cur.Description, tc.Description, StringComparison.Ordinal)
                || cur.DurationMs != tc.DurationMs
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
            if (IsSchemaSnapshotCarrier(msg))
                continue;

            // 缺 Id 的消息无法稳定标识（@key / 增量对账都依赖它），跳过而非回写：
            // 上层写者保证 Id 非空，投影层不得修改执行期消息字段（docs/architecture/04 §5.2）。
            if (string.IsNullOrEmpty(msg.Id))
                continue;

            var id = msg.Id!;
            var isCompacted = lastSummaryIndex >= 0 && indexInSession < lastSummaryIndex;

            if (knownIds.Contains(id))
            {
                // 已消费的排队消息：从待消费列表移入主时间线
                var pendingIndex = _pending.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (pendingIndex >= 0)
                {
                    var pendingVm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
                    if (pendingVm.IsPendingInbox)
                        continue; // 仍排队：保持原位（顺序由 session 顺序保证）

                    _pending.RemoveAt(pendingIndex);
                    pendingVm.IsCompacted = isCompacted;
                    var promoted = new TimelineItem
                    {
                        Key = pendingVm.Id,
                        Kind = ResolveKind(pendingVm),
                        Message = pendingVm
                    };
                    _items.Add(promoted);
                    _itemByMessageId[pendingVm.Id] = promoted;
                    added = true;
                    continue;
                }

                // 已知消息：压缩发生（摘要出现/移动）时同步更新折叠态
                var existing = FindItemForMessageId(id);
                if (existing?.Message != null && existing.Message.IsCompacted != isCompacted)
                {
                    existing.Message.IsCompacted = isCompacted;
                    // 折叠态属于条目级展示变化（非 Loop 内分段），Touch 即可
                    existing.Touch();
                    added = true;
                }
                continue;
            }

            var vm = MessageViewModelFactory.FromSessionMessage(msg, sessionId, isComplete: true);
            vm.IsCompacted = isCompacted;

            // 排队中的入站 user 消息不进主时间线，单独投影为待消费列表
            if (vm.IsPendingInbox)
            {
                _pending.Add(vm);
                _itemByMessageId[vm.Id] = null;
                knownIds.Add(id);
                added = true;
                continue;
            }

            var kind = ResolveKind(vm);

            if (kind == TimelineItemKind.AssistantTurn)
            {
                SyncAssistantMessage(msg, sessionId, isComplete: true);
                knownIds.Add(id);
                added = true;
                continue;
            }

            var plainItem = new TimelineItem
            {
                Key = vm.Id,
                Kind = kind,
                Message = vm
            };
            _items.Add(plainItem);
            _itemByMessageId[vm.Id] = plainItem;
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
    /// 在时间线中按消息 Id 查找对应的展示项。
    /// </summary>
    private TimelineItem? FindItemForMessageId(string id) =>
        _itemByMessageId.TryGetValue(id, out var item) ? item : null;

    /// <summary>
    /// 按消息 Id 取时间线中已投影的视图模型（Loop 内的在 <c>Turn.Messages</c>，其余在 <c>Item.Message</c>）。
    /// <para>供同步器在「不改变完成态」的同步中读取既有完成态：完成态的权威在时间线自身，
    /// 不在执行器指针（指针在回合交界处是陈旧的）。</para>
    /// </summary>
    public bool TryFindMessage(string messageId, out MessageViewModel message)
    {
        message = null!;
        if (string.IsNullOrEmpty(messageId))
            return false;

        var item = FindItemForMessageId(messageId);
        if (item == null)
            return false;

        if (item.Message != null)
        {
            message = item.Message;
            return true;
        }

        var turn = item.Turn;
        if (turn == null)
            return false;

        foreach (var candidate in turn.Messages)
        {
            if (string.Equals(candidate.Id, messageId, StringComparison.Ordinal))
            {
                message = candidate;
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// 已知消息 Id 集合（含排队消息：索引里值为 null 的条目）。
    /// </summary>
    private HashSet<string> CollectKnownMessageIds() =>
        new(_itemByMessageId.Keys, StringComparer.Ordinal);

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
        _turnByKey.TryGetValue(key, out var item) ? item : null;

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
                // 消息并入既有轮次 → 结构变化
                existing.TouchStructure();

                // 索引维护：orphan 条目移除，其消息改指向既有轮次
                foreach (var m in old.Turn.Messages)
                    _itemByMessageId[m.Id] = existing;
            }

            _items.RemoveAt(idx);
            _turnByKey.Remove(old.Key);
            _pendingOrphanKeys.Remove(old.Key);
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
        // 索引维护：旧 key 移除、新 key 登记；消息改指向新条目
        _turnByKey.Remove(old.Key);
        _turnByKey[loopId] = rekeyed;
        _pendingOrphanKeys.Remove(old.Key);
        _pendingOrphanKeys.Remove(loopId);
        if (rekeyed.Turn?.Messages != null)
        {
            foreach (var m in rekeyed.Turn.Messages)
                _itemByMessageId[m.Id] = rekeyed;
        }

        // 换 Key 后视图必须整段重建（@key 变化，旧的 @key 缓存全部失效）
        rekeyed.TouchStructure();
        return rekeyed;
    }

    private void AbsorbOrphanSinglesInto(TimelineItem target, string loopId, string currentMessageId)
    {
        if (target.Turn == null)
            return;

        if (_pendingOrphanKeys.Count > 0)
        {
            foreach (var key in _pendingOrphanKeys.ToList())
            {
                if (!_turnByKey.TryGetValue(key, out var item))
                {
                    _pendingOrphanKeys.Remove(key);
                    continue;
                }
                if (ReferenceEquals(item, target))
                {
                    _pendingOrphanKeys.Remove(key);
                    continue;
                }
                if (item.Kind != TimelineItemKind.AssistantTurn || item.Turn?.Messages == null)
                    continue;

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
                    _itemByMessageId[m.Id] = target;
                }

                var idx = _items.IndexOf(item);
                if (idx >= 0)
                    _items.RemoveAt(idx);
                _turnByKey.Remove(key);
                _pendingOrphanKeys.Remove(key);
            }
        }

        target.Turn.LoopId = loopId;
        target.Turn.IsComplete = target.Turn.Messages.All(m => m.IsComplete);
        _turnByKey[loopId] = target;
        _pendingOrphanKeys.Remove(loopId);
        // 移出循环，只调一次
        RenumberLoopIndexes();
        // 吸收 orphan 会改变轮次内消息集合 → 结构变化
        target.TouchStructure();
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
        _turnByKey[key] = item;
        if (seedVm != null)
            _itemByMessageId[seedVm.Id] = item;
        if (key.StartsWith("single-", StringComparison.Ordinal))
            _pendingOrphanKeys.Add(key);
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
        if (vm.IsContextUpdate)
            return TimelineItemKind.ContextUpdate;
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

    /// <summary>
    /// 旧版执行器会插入空 system 消息仅承载 schema_snapshot；不展示、不 reconcile。
    /// </summary>
    private static bool IsSchemaSnapshotCarrier(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(msg.Content))
            return false;
        if (msg.Parts is { Count: > 0 })
            return false;
        return msg.Metadata?.ContainsKey("schema_snapshot") == true;
    }

    private void RaiseChanged() => Changed?.Invoke();
}
