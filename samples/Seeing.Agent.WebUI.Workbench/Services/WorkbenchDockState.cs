using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 会话工作台停靠状态（Scoped，circuit 维度，纯 C# 无 Blazor 依赖）。
/// <para>
/// <b>按会话分桶</b>：切换 active 会话不串场。每桶持有左右两个 dock 的实例列表、激活键与宽度。
/// 所有写操作都是同步的（UI 事件驱动），读者拿到不可变投影 <see cref="WorkbenchSessionLayout"/>。
/// </para>
/// <para>
/// <b>不认识的实例</b>（来源模块已停用 / 视图已消失）由 <see cref="Prune"/> 清掉，
/// 调用方（dock 宿主）在每次目录变化时执行一次。
/// </para>
/// </summary>
public sealed class WorkbenchDockState
{
    /// <summary>宽度百分比允许区间。</summary>
    public const double MinWidthPercent = 20;

    /// <summary>宽度百分比允许区间。</summary>
    public const double MaxWidthPercent = 75;

    /// <summary>
    /// 左右 dock 宽度之和的上限：聊天列必须始终留出这部分空间。
    /// <para>
    /// 不加预算时两个 dock 各 50% 会把主列压成 0 宽——表现为「聊天区不见了」，
    /// 但用户从没点过全屏开关，无从理解发生了什么。
    /// </para>
    /// </summary>
    public const double MaxCombinedPercent = 75;

    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _bySession = new(StringComparer.Ordinal);
    private readonly double _defaultWidth;

    /// <summary>创建状态。</summary>
    /// <param name="defaultWidthPercent">无持久化宽度时的缺省百分比。</param>
    public WorkbenchDockState(double defaultWidthPercent = WorkbenchSizing.DefaultPercentValue)
    {
        _defaultWidth = Clamp(defaultWidthPercent);
    }

    /// <summary>布局变化通知（任意会话的任意 dock）。</summary>
    public event Action<string>? Changed;

    /// <summary>取某会话的布局投影（未知会话返回空布局）。</summary>
    public WorkbenchSessionLayout GetLayout(string sessionId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(sessionId) || !_bySession.TryGetValue(sessionId, out var bucket))
                return WorkbenchSessionLayout.Empty with
                {
                    LeftWidthPercent = _defaultWidth,
                    RightWidthPercent = _defaultWidth
                };

            return bucket.ToLayout();
        }
    }

    /// <summary>
    /// 打开工作台：已在该 dock 存在则仅激活；否则追加到 dock 末尾并激活。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="dock">目标 dock。</param>
    /// <param name="contribution">工作台声明（标题 / 视图键取自它）。</param>
    /// <param name="openPayload">打开载荷（可空；非空时覆盖旧值）。</param>
    /// <returns>实际生效的实例键；会话 id 为空时返回 null。</returns>
    public WorkbenchInstanceKey? Open(
        string sessionId,
        WorkbenchDock dock,
        WorkbenchContribution contribution,
        JsonElement? openPayload = null)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var key = new WorkbenchInstanceKey(dock, contribution.Id);
        var width = Clamp((contribution.Sizing ?? WorkbenchSizing.Default).DefaultPercent);

        lock (_gate)
        {
            var bucket = GetOrCreateBucketLocked(sessionId, width);
            bucket.Upsert(key, contribution.Title, contribution.ViewKey, openPayload, width);
            bucket.SetActive(dock, key);
        }

        RaiseChanged(sessionId);
        return key;
    }

    /// <summary>激活某 dock 内已存在的实例；不存在则无操作。</summary>
    public void Activate(string sessionId, WorkbenchInstanceKey key)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool changed;
        lock (_gate)
        {
            changed = _bySession.TryGetValue(sessionId, out var bucket) && bucket.SetActive(key.Dock, key);
        }

        if (changed)
            RaiseChanged(sessionId);
    }

    /// <summary>关闭实例；关闭激活项时把激活键交给该 dock 内的下一个（优先后一个）。</summary>
    public void Close(string sessionId, WorkbenchInstanceKey key)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool changed;
        lock (_gate)
        {
            changed = _bySession.TryGetValue(sessionId, out var bucket) && bucket.Remove(key);
            if (changed && bucket is { IsEmpty: true })
            {
                // 关掉最后一个工作台：同步撤销「聊天隐藏」，否则界面变成白屏
                bucket.SetChatHidden(false);
                _bySession.Remove(sessionId);
            }
        }

        if (changed)
            RaiseChanged(sessionId);
    }

    /// <summary>设置某 dock 宽度（夹到 [<see cref="MinWidthPercent"/>, <see cref="MaxWidthPercent"/>]）。</summary>
    public void SetWidth(string sessionId, WorkbenchDock dock, double percent)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var width = Clamp(percent);
        bool changed;
        lock (_gate)
        {
            var bucket = GetOrCreateBucketLocked(sessionId, _defaultWidth);
            changed = bucket.SetWidth(dock, width);
        }

        if (changed)
            RaiseChanged(sessionId);
    }

    /// <summary>记录工作台上报的可恢复状态与上下文摘要。</summary>
    public void ApplyViewReport(string sessionId, WorkbenchInstanceKey key, WorkbenchViewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool changed;
        lock (_gate)
        {
            changed = _bySession.TryGetValue(sessionId, out var bucket) && bucket.ApplyReport(key, report);
        }

        if (changed)
            RaiseChanged(sessionId);
    }

    /// <summary>
    /// 取走（并清除）某实例的打开载荷。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须"取走"而不是只读</b>：<c>OpenPayload</c> 的契约是<b>一次性指令</b>
    /// （见 <see cref="WorkbenchInstance"/> 的注释），但早期实现只存不清 —— 于是工作台组件
    /// 每次挂载都会重放它。浏览器工作台的重放动作是"导航到 url"，表现为
    /// <b>切到别的标签再切回来、或刷新页面后，页面整页重新加载、滚动位置与表单全丢</b>
    /// （用户实测报障："切换回来浏览器会整个刷新，它不会保持状态吗"）。
    /// </para>
    /// <para>
    /// <b>为什么要落在这里而不是组件里</b>：组件在切 tab 时会被销毁重建
    /// （dock 只渲染激活项），任何存在组件字段里的"已消费"标记都会随之消失 ——
    /// 那正是缺陷的成因。消费必须记在<b>实例</b>上，它比组件活得久。
    /// </para>
    /// </remarks>
    /// <returns>载荷（已同时从实例上清除）；没有载荷或实例不存在时为 <see langword="null"/>。</returns>
    public JsonElement? TakeOpenPayload(string sessionId, WorkbenchInstanceKey key)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        JsonElement? taken = null;
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out var bucket))
                taken = bucket.TakeOpenPayload(key);
        }

        return taken;
    }

    /// <summary>
    /// 清掉不再可用的实例（模块停用 / 视图消失 / 工作台声明被撤回）。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="isKnown">
    /// 判定谓词：给定工作台 id，返回它当前是否仍可用。不可用的实例被移除。
    /// </param>
    public void Prune(string sessionId, Func<string, bool> isKnown)
    {
        ArgumentNullException.ThrowIfNull(isKnown);
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool changed;
        lock (_gate)
        {
            changed = false;
            if (!_bySession.TryGetValue(sessionId, out var bucket))
                return;

            foreach (var dock in new[] { WorkbenchDock.Left, WorkbenchDock.Right })
            {
                foreach (var instance in bucket.Instances(dock).ToArray())
                {
                    if (isKnown(instance.Key.WorkbenchId))
                        continue;

                    bucket.Remove(instance.Key);
                    changed = true;
                }
            }

            // 全部实例都没了：撤销「聊天隐藏」，否则用户会看到白屏且找不到恢复入口
            if (changed && bucket.IsEmpty)
            {
                bucket.SetChatHidden(false);
                _bySession.Remove(sessionId);
            }
        }

        if (changed)
            RaiseChanged(sessionId);
    }

    /// <summary>
    /// 把某个实例移到另一个 dock 的末尾并激活（用户点「换到另一侧」）。
    /// <para>
    /// 目标 dock 若已开着同一个工作台，则<b>合并</b>（不产生第二个实例）：保留目标那份，
    /// 源那份关闭——否则会出现两个同 id 标签、RPC 还要靠 dock 消歧，体验与实现都更糟。
    /// </para>
    /// </summary>
    /// <returns>移动后的实例键；实例不存在返回 null。</returns>
    public WorkbenchInstanceKey? MoveToOtherDock(string sessionId, WorkbenchInstanceKey key)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        WorkbenchInstanceKey? result = null;
        bool changed;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var bucket))
                return null;

            var target = key.Dock == WorkbenchDock.Left ? WorkbenchDock.Right : WorkbenchDock.Left;
            var source = bucket.Instances(key.Dock).FirstOrDefault(i => i.Key.Equals(key));
            if (source is null)
                return null;

            var existingInTarget = bucket.Instances(target).FirstOrDefault(i =>
                string.Equals(i.Key.WorkbenchId, key.WorkbenchId, StringComparison.Ordinal));

            if (existingInTarget is not null)
            {
                // 目标已有同一个工作台：关掉源那份，激活目标那份
                bucket.Remove(key);
                bucket.SetActive(target, existingInTarget.Key);
                result = existingInTarget.Key;
            }
            else
            {
                var moved = new WorkbenchInstanceKey(target, key.WorkbenchId, key.Ordinal);
                var targetWidth = target == WorkbenchDock.Left ? bucket.LeftWidth : bucket.RightWidth;
                bucket.Remove(key);
                bucket.Upsert(
                    moved, source.Title, source.ViewKey, source.OpenPayload, targetWidth, source.ViewState);
                bucket.SetActive(target, moved);
                result = moved;
            }

            changed = true;
            if (bucket.IsEmpty)
                _bySession.Remove(sessionId);
        }

        if (changed)
            RaiseChanged(sessionId);

        return result;
    }

    /// <summary>
    /// 设置聊天主列是否隐藏（工作区独占全宽）。
    /// </summary>
    /// <returns>是否发生变更（相同值不触发事件）。</returns>
    public bool SetChatHidden(string sessionId, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        bool changed;
        lock (_gate)
        {
            var bucket = GetOrCreateBucketLocked(sessionId, _defaultWidth);
            changed = bucket.SetChatHidden(hidden);

            // 没有任何工作台时「隐藏聊天」等于白屏：一并撤销，避免用户无法恢复
            if (changed && hidden && bucket.IsEmpty)
            {
                bucket.SetChatHidden(false);
                changed = false;
            }
        }

        if (changed)
            RaiseChanged(sessionId);

        return changed;
    }

    /// <summary>聊天主列是否隐藏（无布局时为 false）。</summary>
    public bool IsChatHidden(string sessionId)
    {
        lock (_gate)
            return !string.IsNullOrEmpty(sessionId)
                   && _bySession.TryGetValue(sessionId, out var bucket)
                   && bucket.ChatHidden;
    }

    /// <summary>清空某会话的全部布局（会话删除 / 页面卸载）。</summary>
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool removed;
        lock (_gate)
            removed = _bySession.Remove(sessionId);

        if (removed)
            RaiseChanged(sessionId);
    }

    /// <summary>
    /// 从持久化布局恢复（localStorage 快照）。仅恢复成员、激活项与宽度；
    /// 视图键与标题由调用方按当前声明重新填充（旧快照里的视图键可能已失效）。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="restored">恢复的实例（调用方已按当前声明校验并填充标题/视图键）。</param>
    public void Restore(string sessionId, WorkbenchSessionLayout restored)
    {
        ArgumentNullException.ThrowIfNull(restored);
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
        {
            if (restored.Left.Count == 0 && restored.Right.Count == 0)
            {
                _bySession.Remove(sessionId);
                return;
            }

            var bucket = new Bucket(
                Clamp(restored.LeftWidthPercent),
                Clamp(restored.RightWidthPercent));

            // 恢复期间逐条 Upsert 会各自触发预算压缩（中间态），因此先关预算、最后统一校正
            bucket.BeginRestore();

            foreach (var instance in restored.Left)
                bucket.Upsert(instance.Key, instance.Title, instance.ViewKey, instance.OpenPayload, bucket.LeftWidth);
            foreach (var instance in restored.Right)
                bucket.Upsert(instance.Key, instance.Title, instance.ViewKey, instance.OpenPayload, bucket.RightWidth);

            bucket.SetActiveFromText(WorkbenchDock.Left, restored.ActiveLeft);
            bucket.SetActiveFromText(WorkbenchDock.Right, restored.ActiveRight);
            // 聊天隐藏只在确实有工作台可看时才恢复（否则会恢复成白屏）
            bucket.SetChatHidden(restored.ChatHidden);
            bucket.EndRestore();

            _bySession[sessionId] = bucket;
        }

        RaiseChanged(sessionId);
    }

    /// <summary>是否存在任一非空 dock（用于「是否需要渲染容器」）。</summary>
    public bool HasAny(string sessionId)
    {
        var layout = GetLayout(sessionId);
        return layout.Left.Count > 0 || layout.Right.Count > 0;
    }

    private Bucket GetOrCreateBucketLocked(string sessionId, double width)
    {
        if (_bySession.TryGetValue(sessionId, out var bucket))
            return bucket;

        bucket = new Bucket(Clamp(width), Clamp(width));
        _bySession[sessionId] = bucket;
        return bucket;
    }

    private static double Clamp(double percent)
        => double.IsNaN(percent) ? WorkbenchSizing.Default.DefaultPercent : Math.Clamp(percent, MinWidthPercent, MaxWidthPercent);

    private void RaiseChanged(string sessionId)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(sessionId); }
            catch { /* 订阅者（UI 组件）自己负责；不得抛回写者 */ }
        }
    }

    /// <summary>单会话的可变桶（仅在 <see cref="_gate"/> 内访问）。</summary>
    private sealed class Bucket
    {
        private readonly List<WorkbenchInstance> _left = [];
        private readonly List<WorkbenchInstance> _right = [];
        private string? _activeLeft;
        private string? _activeRight;

        public Bucket(double leftWidth, double rightWidth)
        {
            LeftWidth = leftWidth;
            RightWidth = rightWidth;
        }

        public double LeftWidth { get; private set; }

        public double RightWidth { get; private set; }

        /// <summary>聊天主列是否隐藏。</summary>
        public bool ChatHidden { get; private set; }

        /// <summary>恢复期间暂停预算压缩（逐条 Upsert 的中间态不该反复压缩）。</summary>
        private bool _suppressRebalance;

        public bool IsEmpty => _left.Count == 0 && _right.Count == 0;

        public IReadOnlyList<WorkbenchInstance> Instances(WorkbenchDock dock)
            => dock == WorkbenchDock.Left ? _left : _right;

        public WorkbenchSessionLayout ToLayout()
            => new(
                _left.Select(instance => WithActive(instance, _activeLeft)).ToArray(),
                _right.Select(instance => WithActive(instance, _activeRight)).ToArray(),
                _activeLeft,
                _activeRight,
                LeftWidth,
                RightWidth)
            {
                ChatHidden = ChatHidden
            };

        /// <summary>设置聊天隐藏；返回是否变更。</summary>
        public bool SetChatHidden(bool hidden)
        {
            if (ChatHidden == hidden)
                return false;

            ChatHidden = hidden;
            return true;
        }

        private static WorkbenchInstance WithActive(WorkbenchInstance instance, string? activeText)
            => instance with
            {
                IsActive = string.Equals(instance.Key.ToString(), activeText, StringComparison.Ordinal)
            };

        public void Upsert(
            WorkbenchInstanceKey key,
            string title,
            string viewKey,
            JsonElement? openPayload,
            double width,
            IReadOnlyDictionary<string, string>? viewState = null)
        {
            var list = key.Dock == WorkbenchDock.Left ? _left : _right;
            var index = list.FindIndex(instance => instance.Key.Equals(key));

            var next = new WorkbenchInstance(
                key,
                title,
                viewKey,
                openPayload,
                // 显式传入（换边搬状态）优先；否则沿用该 dock 内既有实例的状态
                viewState ?? (index >= 0 ? list[index].ViewState : null));

            var isFirstInDock = list.Count == 0;

            if (index >= 0)
                list[index] = next;
            else
                list.Add(next);

            // 宽度只在「该 dock 的第一个实例」时采用声明值：
            // 之后再加标签、或重开同一个工作台，都不应把用户拖好的宽度重置回默认
            if (isFirstInDock)
            {
                if (key.Dock == WorkbenchDock.Left)
                    LeftWidth = Clamp(width);
                else
                    RightWidth = Clamp(width);
            }

            RebalanceWidths(changedDock: key.Dock);
        }

        /// <summary>
        /// 按预算校正一次宽度（批量恢复后用：逐条 Upsert 期间的中间态不该反复压缩）。
        /// 压缩比例按两侧现有宽度等比分摊，保留用户原本的相对关系。
        /// </summary>
        public void NormalizeWidths()
        {
            if (_left.Count == 0 || _right.Count == 0)
                return;

            var total = LeftWidth + RightWidth;
            if (total <= MaxCombinedPercent)
                return;

            var scale = MaxCombinedPercent / total;
            LeftWidth = Clamp(LeftWidth * scale);
            RightWidth = Clamp(RightWidth * scale);

            // 夹到下限后仍可能超预算：把多出来的量从较大的一侧再扣
            if (LeftWidth + RightWidth > MaxCombinedPercent)
            {
                var overflow = LeftWidth + RightWidth - MaxCombinedPercent;
                if (LeftWidth >= RightWidth)
                    LeftWidth = Clamp(LeftWidth - overflow);
                else
                    RightWidth = Clamp(RightWidth - overflow);
            }
        }

        /// <summary>进入恢复模式：暂停预算压缩（逐条 Upsert 期间的中间态不该反复压缩）。</summary>
        public void BeginRestore() => _suppressRebalance = true;

        /// <summary>结束恢复模式并统一校正一次宽度。</summary>
        public void EndRestore()
        {
            _suppressRebalance = false;
            NormalizeWidths();
        }

        /// <summary>
        /// 保证两 dock 宽度之和不超过 <see cref="MaxCombinedPercent"/>：超出时按比例压缩**另一侧**。
        /// <para>压缩的是另一侧而不是刚被设置的那一侧：用户正在拖的那一边应当忠实跟随指针。</para>
        /// <para>
        /// 预算只在<b>两侧都有实例</b>时生效：只有一侧开着工作台时，那一侧可以一路拖到 75% ——
        /// 此时没有「被挤没」的对话区可言（对话区本来就与它分享剩余空间）。
        /// </para>
        /// </summary>
        private void RebalanceWidths(WorkbenchDock changedDock)
        {
            if (_suppressRebalance)
                return;

            if (_left.Count == 0 || _right.Count == 0)
                return;

            if (LeftWidth + RightWidth <= MaxCombinedPercent)
                return;

            var other = MaxCombinedPercent - (changedDock == WorkbenchDock.Left ? LeftWidth : RightWidth);
            var clampedOther = Clamp(other);

            if (changedDock == WorkbenchDock.Left)
                RightWidth = clampedOther;
            else
                LeftWidth = clampedOther;

            // 另一侧已到下限仍超预算：反过来压缩被操作侧，确保主列永远留得住
            if (LeftWidth + RightWidth > MaxCombinedPercent)
            {
                var allowed = MaxCombinedPercent - Math.Min(LeftWidth, RightWidth);
                if (changedDock == WorkbenchDock.Left)
                    LeftWidth = Clamp(allowed);
                else
                    RightWidth = Clamp(allowed);
            }
        }

        public bool Remove(WorkbenchInstanceKey key)
        {
            var list = key.Dock == WorkbenchDock.Left ? _left : _right;
            var index = list.FindIndex(instance => instance.Key.Equals(key));
            if (index < 0)
                return false;

            list.RemoveAt(index);

            var activeText = key.Dock == WorkbenchDock.Left ? _activeLeft : _activeRight;
            if (!string.Equals(activeText, key.ToString(), StringComparison.Ordinal))
                return true;

            // 关闭激活项：把激活权交给后一个，没有则前一个
            var nextActive = list.Count == 0
                ? null
                : list[Math.Min(index, list.Count - 1)].Key.ToString();

            if (key.Dock == WorkbenchDock.Left)
                _activeLeft = nextActive;
            else
                _activeRight = nextActive;

            return true;
        }

        public bool SetActive(WorkbenchDock dock, WorkbenchInstanceKey key)
        {
            var list = dock == WorkbenchDock.Left ? _left : _right;
            if (!list.Any(instance => instance.Key.Equals(key)))
                return false;

            var text = key.ToString();
            if (dock == WorkbenchDock.Left)
            {
                if (string.Equals(_activeLeft, text, StringComparison.Ordinal))
                    return false;
                _activeLeft = text;
            }
            else
            {
                if (string.Equals(_activeRight, text, StringComparison.Ordinal))
                    return false;
                _activeRight = text;
            }

            return true;
        }

        public void SetActiveFromText(WorkbenchDock dock, string? text)
        {
            var list = dock == WorkbenchDock.Left ? _left : _right;
            var resolved = list.FirstOrDefault(instance =>
                               string.Equals(instance.Key.ToString(), text, StringComparison.Ordinal))?.Key.ToString()
                           ?? list.FirstOrDefault()?.Key.ToString();

            if (dock == WorkbenchDock.Left)
                _activeLeft = resolved;
            else
                _activeRight = resolved;
        }

        public bool SetWidth(WorkbenchDock dock, double percent)
        {
            if (dock == WorkbenchDock.Left)
            {
                if (Math.Abs(LeftWidth - percent) < 0.01)
                    return false;
                LeftWidth = percent;
            }
            else
            {
                if (Math.Abs(RightWidth - percent) < 0.01)
                    return false;
                RightWidth = percent;
            }

            RebalanceWidths(changedDock: dock);
            return true;
        }

        public bool ApplyReport(WorkbenchInstanceKey key, WorkbenchViewReport report)
        {
            var list = key.Dock == WorkbenchDock.Left ? _left : _right;
            var index = list.FindIndex(instance => instance.Key.Equals(key));
            if (index < 0)
                return false;

            var current = list[index];
            var viewState = report.ViewState ?? current.ViewState;

            if (SameState(current.ViewState, viewState))
                return false;

            list[index] = current with { ViewState = viewState };
            return true;
        }

        /// <summary>取走（并清除）某实例的打开载荷；实例不存在或无载荷返回 <see langword="null"/>。</summary>
        /// <remarks>
        /// 清除是契约的一部分（"一次性指令"）：不清的话组件每次重建都会重放导航，
        /// 表现为切换标签 / 刷新后页面整页重载。
        /// </remarks>
        public System.Text.Json.JsonElement? TakeOpenPayload(WorkbenchInstanceKey key)
        {
            var list = key.Dock == WorkbenchDock.Left ? _left : _right;
            var index = list.FindIndex(instance => instance.Key.Equals(key));
            if (index < 0)
                return null;

            var payload = list[index].OpenPayload;
            if (payload is null)
                return null;

            list[index] = list[index] with { OpenPayload = null };
            return payload;
        }

        private static bool SameState(
            IReadOnlyDictionary<string, string>? left,
            IReadOnlyDictionary<string, string>? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Count != right.Count)
                return false;

            foreach (var (key, value) in left)
            {
                if (!right.TryGetValue(key, out var other)
                    || !string.Equals(value, other, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static double Clamp(double percent)
            => double.IsNaN(percent) ? WorkbenchSizing.Default.DefaultPercent : Math.Clamp(percent, MinWidthPercent, MaxWidthPercent);
    }
}
