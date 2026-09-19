using System;
using System.Collections.Generic;
using System.Linq;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>侧栏分区条目（由窗口快照 + 页面侧执行态投影的纯数据，无副作用）。</summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="Title">会话标题。</param>
/// <param name="Relation">在所属组内的关系角色。</param>
/// <param name="IsAnchor">是否为当前锚点（主线末端）。</param>
/// <param name="IsExecuting">是否执行中（由页面侧收集的运行态，registry 不持权威态）。</param>
/// <param name="ParentSessionId">来源会话 ID（边的来源方向）。</param>
/// <param name="Label">成员标签（如 trim 备份前缀）。</param>
/// <param name="IsActive">是否为当前活跃会话。</param>
/// <param name="PendingPermissions">该会话在途审批数（0 时不渲染徽标）。</param>
public sealed record SidebarEntry(
    string SessionId,
    string Title,
    SessionRelation Relation,
    bool IsAnchor,
    bool IsExecuting,
    string? ParentSessionId,
    string? Label,
    bool IsActive,
    int PendingPermissions = 0);

/// <summary>侧栏分区（固定顺序：主线历史 → 派生 → 子会话；空分区省略）。</summary>
/// <param name="Title">分区标题。</param>
/// <param name="Section">分区枚举。</param>
/// <param name="Entries">分区内条目（执行中置顶，其余保持输入顺序）。</param>
public sealed record SidebarSection(
    string Title,
    SessionSection Section,
    IReadOnlyList<SidebarEntry> Entries);

/// <summary>
/// 侧栏分区的纯映射逻辑（无 DI、无副作用，便于单测）。
/// <para>
/// 分区顺序固定为 主线历史（<see cref="SessionSection.MainLineHistory"/>）→
/// 派生（<see cref="SessionSection.Derived"/>）→ 子会话（<see cref="SessionSection.Children"/>）；
/// 分区内执行中条目置顶，其余按输入顺序（=成员 <c>Order</c>/更新时间）稳定排列；空分区省略。
/// </para>
/// </summary>
public static class SessionSidebarView
{
    /// <summary>主线历史分区标题。</summary>
    public const string MainLineHistoryTitle = "主线历史";

    /// <summary>派生分区标题。</summary>
    public const string DerivedTitle = "派生";

    /// <summary>子会话分区标题。</summary>
    public const string ChildrenTitle = "子会话";

    /// <summary>按固定分区顺序构建侧栏分区，空分区省略。</summary>
    public static IReadOnlyList<SidebarSection> Build(IEnumerable<SidebarEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries as IReadOnlyList<SidebarEntry> ?? entries.ToList();
        var sections = new List<SidebarSection>(3);
        AddSection(sections, list, SessionSection.MainLineHistory, MainLineHistoryTitle);
        AddSection(sections, list, SessionSection.Derived, DerivedTitle);
        AddSection(sections, list, SessionSection.Children, ChildrenTitle);
        return sections;
    }

    /// <summary>追加单个分区（无匹配条目则省略）；执行中置顶为稳定排序。</summary>
    private static void AddSection(
        List<SidebarSection> sections,
        IReadOnlyList<SidebarEntry> entries,
        SessionSection section,
        string title)
    {
        var matched = entries
            .Where(e => SessionRelationView.Section(e.Relation, e.IsAnchor) == section)
            .OrderByDescending(e => e.IsExecuting)
            .ToList();

        if (matched.Count > 0)
            sections.Add(new SidebarSection(title, section, matched));
    }
}
