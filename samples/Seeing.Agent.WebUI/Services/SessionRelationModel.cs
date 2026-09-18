using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>关系树节点（由组快照 + 会话标题投影的纯数据，无副作用）。</summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="Title">会话标题。</param>
/// <param name="Relation">在所属组内的关系角色。</param>
/// <param name="IsAnchor">是否为当前锚点（主线末端）。</param>
/// <param name="IsExecuting">是否执行中（由页面侧收集的运行态，registry 不持权威态）。</param>
/// <param name="ParentSessionId">来源会话 ID（边的来源方向）。</param>
/// <param name="Label">成员标签（如 trim 备份前缀）。</param>
/// <param name="IsActive">是否为当前活跃会话。</param>
/// <param name="SuccessorTitle">交接前任（<see cref="SessionRelation.HandoffPredecessor"/>）后继会话标题；无后继时为 null。</param>
public sealed record RelationNode(
    string SessionId,
    string Title,
    SessionRelation Relation,
    bool IsAnchor,
    bool IsExecuting,
    string? ParentSessionId,
    string? Label,
    bool IsActive,
    string? SuccessorTitle = null);

/// <summary>关系抽屉中节点可触发的操作。</summary>
public enum RelationNodeAction
{
    /// <summary>切换为活跃会话。</summary>
    Select,
    /// <summary>重命名。</summary>
    Rename,
    /// <summary>在新标签打开。</summary>
    OpenNewTab,
    /// <summary>按关系规则删除。</summary>
    Delete
}

/// <summary>关系树展平行（界面渲染用，无副作用）。</summary>
/// <param name="Node">节点。</param>
/// <param name="Depth">缩进层级（主线为 0，派生/子会话为 1）。</param>
/// <param name="Source">来源提示：null 表示来源存在或无需展示；<see cref="RelationTreeModel.SourceMissingText"/> 表示来源已删除。</param>
public sealed record RelationRow(RelationNode Node, int Depth, string? Source);

/// <summary>
/// 会话关系树的纯映射模型（无 DI、无副作用，便于单测）。
/// <para>
/// 主线链以锚点为终点，沿 <see cref="RelationNode.ParentSessionId"/> 反向回溯，
/// 仅纳入 <see cref="SessionRelation.HandoffSuccessor"/>/<see cref="SessionRelation.HandoffPredecessor"/> 节点；
/// 不在链上的候选（旧组/断链）按输入顺序（<c>Order</c>）平铺追加到锚点之前，
/// 锚点缺失或回溯断裂时退化为按输入顺序平铺的“主线历史”，不抛异常。
/// </para>
/// <para>输入集合应已按成员 <c>Order</c> 排序，模型按输入顺序保留派生/子会话次序。</para>
/// </summary>
public sealed class RelationTreeModel
{
    /// <summary>完整主线链（最早前任 → 锚点；旧组候选平铺于锚点之前）。</summary>
    public IReadOnlyList<RelationNode> MainLine { get; init; } = Array.Empty<RelationNode>();

    /// <summary>派生会话（<see cref="SessionRelation.Fork"/>，含 trim 备份），按输入顺序。</summary>
    public IReadOnlyList<RelationNode> Derived { get; init; } = Array.Empty<RelationNode>();

    /// <summary>子代理会话（<see cref="SessionRelation.Child"/>，排除退化锚点），按输入顺序。</summary>
    public IReadOnlyList<RelationNode> Children { get; init; } = Array.Empty<RelationNode>();

    /// <summary>派生会话数量。</summary>
    public int DerivedCount => Derived.Count;

    /// <summary>子代理会话数量。</summary>
    public int ChildCount => Children.Count;

    /// <summary>来源缺失（父不存在）时的来源行文案。</summary>
    public const string SourceMissingText = "来源已删除";

    /// <summary>
    /// 展平关系树：主线链（含各前任）为 0 层，每个主线节点下挂其派生/子会话为 1 层；
    /// 未被主线节点直接挂载的派生/子会话按输入顺序追加，来源是否缺失基于**全部节点**
    /// （主线 + 派生 + 子会话）判定——父为 Fork/Child 时不算缺失。
    /// </summary>
    public IReadOnlyList<RelationRow> BuildRows()
    {
        var rows = new List<RelationRow>();
        var mainIds = new HashSet<string>(MainLine.Select(n => n.SessionId), StringComparer.Ordinal);
        var nested = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in MainLine)
        {
            rows.Add(new RelationRow(node, 0, null));

            foreach (var child in Derived.Where(n => Matches(n.ParentSessionId, node.SessionId)))
            {
                rows.Add(new RelationRow(child, 1, null));
                nested.Add(child.SessionId);
            }

            foreach (var child in Children.Where(n => Matches(n.ParentSessionId, node.SessionId)))
            {
                rows.Add(new RelationRow(child, 1, null));
                nested.Add(child.SessionId);
            }
        }

        // 剩余节点（未挂在主线节点下）：父存在则不标缺失，否则标“来源已删除”
        foreach (var orphan in Derived.Concat(Children).Where(n => !nested.Contains(n.SessionId)))
        {
            var sourceExists = !string.IsNullOrEmpty(orphan.ParentSessionId)
                               && (mainIds.Contains(orphan.ParentSessionId!)
                                   || Derived.Any(n => string.Equals(n.SessionId, orphan.ParentSessionId, StringComparison.Ordinal))
                                   || Children.Any(n => string.Equals(n.SessionId, orphan.ParentSessionId, StringComparison.Ordinal)));
            rows.Add(new RelationRow(orphan, 1, sourceExists ? null : SourceMissingText));
        }

        return rows;
    }

    private static bool Matches(string? parentId, string sessionId)
        => string.Equals(parentId, sessionId, StringComparison.Ordinal);

    /// <summary>由节点集合构建关系树模型。</summary>
    public static RelationTreeModel Build(IEnumerable<RelationNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var all = ResolveSuccessorTitles(nodes.ToList());
        var lookup = BuildLookup(all);
        var mainLine = BuildMainLine(all, lookup);

        return new RelationTreeModel
        {
            MainLine = mainLine,
            Derived = all.Where(n => n.Relation == SessionRelation.Fork && !n.IsAnchor).ToList(),
            Children = all.Where(n => n.Relation == SessionRelation.Child && !n.IsAnchor).ToList()
        };
    }

    /// <summary>
    /// 顶部关系条渲染项：主线 ≤ <paramref name="max"/> 个时全部返回（溢出 0）；
    /// 超过时返回“首个 + 锚点”，溢出为其余数量。
    /// </summary>
    public static (IReadOnlyList<RelationNode> Visible, int Overflow) BarItems(
        IReadOnlyList<RelationNode> mainLine, int max = 3)
    {
        if (mainLine is null || mainLine.Count == 0)
            return (Array.Empty<RelationNode>(), 0);

        if (mainLine.Count <= max)
            return (mainLine, 0);

        var anchor = mainLine.LastOrDefault(n => n.IsAnchor) ?? mainLine[^1];
        var first = mainLine[0];

        var visible = string.Equals(first.SessionId, anchor.SessionId, StringComparison.Ordinal)
            ? new List<RelationNode> { anchor }
            : new List<RelationNode> { first, anchor };

        return (visible, mainLine.Count - visible.Count);
    }

    /// <summary>按 SessionId 建索引（重复 ID 保留首个，避免异常）。</summary>
    private static Dictionary<string, RelationNode> BuildLookup(IReadOnlyList<RelationNode> nodes)
    {
        var lookup = new Dictionary<string, RelationNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!lookup.ContainsKey(node.SessionId))
                lookup[node.SessionId] = node;
        }
        return lookup;
    }

    /// <summary>以锚点为终点回溯主线；锚点缺失/断裂时退化为平铺主线历史。</summary>
    /// <remarks>
    /// 能从锚点沿 <see cref="RelationNode.ParentSessionId"/> 反向重建链时使用该链（最早前任 → 锚点），
    /// 并把不在链上的候选（旧组/断链）按输入顺序平铺追加到锚点之前；回溯断裂时全部候选平铺、锚点置末。
    /// 保证旧组（锚点在链首、存在非锚点 HandoffSuccessor）等节点不被丢弃。
    /// </remarks>
    private static IReadOnlyList<RelationNode> BuildMainLine(
        IReadOnlyList<RelationNode> all, IReadOnlyDictionary<string, RelationNode> lookup)
    {
        var anchor = all.FirstOrDefault(n => n.IsAnchor);
        if (anchor is null)
            return FlatHistory(all);

        var chain = new List<RelationNode> { anchor };
        var onChain = new HashSet<string>(StringComparer.Ordinal) { anchor.SessionId };
        var broken = false;

        var parentId = anchor.ParentSessionId;
        while (!string.IsNullOrEmpty(parentId))
        {
            if (!lookup.TryGetValue(parentId!, out var parent))
            {
                broken = true; // 回溯断裂：退化为平铺
                break;
            }

            if (parent.Relation is not (SessionRelation.HandoffSuccessor or SessionRelation.HandoffPredecessor))
                break; // 命中 Fork/Child/None 分支：主线到此为止

            if (!onChain.Add(parent.SessionId))
                break; // 环保护

            chain.Add(parent);
            parentId = parent.ParentSessionId;
        }

        if (broken)
            return FlattenCandidates(all, anchor);

        chain.Reverse(); // 最早前任 → 锚点

        // 旧组/断链候选（不在锚点链上）按输入顺序（Order）平铺追加到锚点之前
        var offChain = all
            .Where(n => IsMainLineCandidate(n.Relation) && !onChain.Contains(n.SessionId))
            .ToList();

        if (offChain.Count == 0)
            return chain;

        var anchorNode = chain[^1];
        return chain.Take(chain.Count - 1).Concat(offChain).Append(anchorNode).ToList();
    }

    /// <summary>平铺主线历史（仅交接候选，排除 Fork/Child 分支），保留输入顺序。</summary>
    private static IReadOnlyList<RelationNode> FlatHistory(IReadOnlyList<RelationNode> all)
        => all.Where(n => IsMainLineCandidate(n.Relation)).ToList();

    /// <summary>平铺主线候选（排除 Fork/Child 与锚点自身后追加锚点），锚点置末。</summary>
    private static IReadOnlyList<RelationNode> FlattenCandidates(
        IReadOnlyList<RelationNode> all, RelationNode anchor)
    {
        var result = all
            .Where(n => IsMainLineCandidate(n.Relation)
                && !string.Equals(n.SessionId, anchor.SessionId, StringComparison.Ordinal))
            .ToList();
        result.Add(anchor);
        return result;
    }

    /// <summary>是否为可纳入主线历史的交接关系候选。</summary>
    private static bool IsMainLineCandidate(SessionRelation relation)
        => relation is SessionRelation.HandoffSuccessor or SessionRelation.HandoffPredecessor;

    /// <summary>为交接前任补全后继标题（反向查找：ParentSessionId==自身 且主线关系）。</summary>
    private static List<RelationNode> ResolveSuccessorTitles(IReadOnlyList<RelationNode> all)
    {
        var result = new List<RelationNode>(all.Count);
        foreach (var node in all)
        {
            if (node.Relation != SessionRelation.HandoffPredecessor || node.SuccessorTitle is not null)
            {
                result.Add(node);
                continue;
            }

            var successor = all.FirstOrDefault(m =>
                !string.Equals(m.SessionId, node.SessionId, StringComparison.Ordinal)
                && string.Equals(m.ParentSessionId, node.SessionId, StringComparison.Ordinal)
                && IsMainLineCandidate(m.Relation));

            result.Add(successor is null ? node : node with { SuccessorTitle = successor.Title });
        }
        return result;
    }
}
