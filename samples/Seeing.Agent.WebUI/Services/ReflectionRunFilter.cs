using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>复盘记录的触发方式筛选。</summary>
public enum ReflectionTriggerFilter
{
    /// <summary>不按触发方式过滤。</summary>
    All = 0,
    TurnNudge,
    Idle,
    Manual
}

/// <summary>
/// 复盘记录的结果筛选。
/// <para>
/// 按「这次复盘留下了什么」分类，而不是按内部状态字符串匹配——后者会把
/// <c>failed:timeout</c> 这类内部形态泄漏到 UI 判定里。
/// </para>
/// </summary>
public enum ReflectionOutcomeFilter
{
    All = 0,

    /// <summary>产出了建议（含已建技能）。</summary>
    WithSuggestions,

    /// <summary>实际写入了技能。</summary>
    CreatedSkills,

    /// <summary>只有失败快照。</summary>
    FailuresOnly
}

/// <summary>复盘记录的时间范围筛选。</summary>
public enum ReflectionRangeFilter
{
    All = 0,
    Today,
    Last7Days,
    Last30Days
}

/// <summary>复盘记录的可排序列。</summary>
public enum ReflectionSortKey
{
    Time = 0,
    Suggestions,
    Skills,
    Tokens
}

/// <summary>
/// 复盘记录列表的筛选与排序（纯函数）。
/// <para>
/// <b>为什么从页面里抽出来</b>：这套条件组合（搜索 × 触发 × 结果 × 时间 × 排序）
/// 是页面唯一的实质逻辑，埋在 <c>@code</c> 里就只能靠人点页面验证。
/// 抽成纯函数后可以直接对边界用例下断言（空搜索、范围边界、排序稳定性）。
/// </para>
/// <para>
/// 在内存里过滤而不是每次改条件都重读磁盘：记录上限 200 条，成本可忽略，
/// 而重读会把「切筛选」变成一次 I/O（还可能撞上正在写入的文件）。
/// </para>
/// </summary>
public static class ReflectionRunFilter
{
    /// <summary>
    /// 应用筛选与排序。
    /// </summary>
    /// <param name="runs">原始记录集合。</param>
    /// <param name="searchText">搜索词（匹配会话 ID / 执行 ID / RunId / 会话标题）。</param>
    /// <param name="trigger">触发方式。</param>
    /// <param name="outcome">结果分类。</param>
    /// <param name="range">时间范围（按本地时间与「现在」比较）。</param>
    /// <param name="sortKey">排序列。</param>
    /// <param name="descending">是否降序。</param>
    /// <param name="sessionTitle">会话标题解析（可为 null，此时只用 ID 匹配）。</param>
    /// <param name="now">当前本地时间（注入以便测试，不读时钟）。</param>
    /// <returns>筛选并排序后的列表。</returns>
    public static IReadOnlyList<ReflectionRunSummary> Apply(
        IEnumerable<ReflectionRunSummary> runs,
        string? searchText,
        ReflectionTriggerFilter trigger,
        ReflectionOutcomeFilter outcome,
        ReflectionRangeFilter range,
        ReflectionSortKey sortKey,
        bool descending,
        Func<string, string>? sessionTitle = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(runs);

        IEnumerable<ReflectionRunSummary> query = runs;

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            query = query.Where(r =>
                Contains(r.SessionId, term)
                || Contains(r.ExecutionId, term)
                || Contains(r.RunId, term)
                || (sessionTitle is not null && Contains(sessionTitle(r.SessionId), term)));
        }

        query = trigger switch
        {
            ReflectionTriggerFilter.TurnNudge => query.Where(r => r.Kind == ReflectionTriggerKind.TurnNudge),
            ReflectionTriggerFilter.Idle => query.Where(r => r.Kind == ReflectionTriggerKind.Idle),
            ReflectionTriggerFilter.Manual => query.Where(r => r.Kind == ReflectionTriggerKind.Manual),
            _ => query
        };

        query = outcome switch
        {
            ReflectionOutcomeFilter.WithSuggestions => query.Where(r => r.Suggestions.Count > 0),
            ReflectionOutcomeFilter.CreatedSkills => query.Where(r => r.CreatedSkills.Count > 0),
            ReflectionOutcomeFilter.FailuresOnly => query.Where(r => r.IsFailure),
            _ => query
        };

        if (range != ReflectionRangeFilter.All)
        {
            var reference = (now ?? DateTimeOffset.Now).ToLocalTime();
            var cutoff = range switch
            {
                // 「今天」用本地日期起点，而不是「24 小时内」：用户说的是日历上的今天。
                ReflectionRangeFilter.Today => new DateTimeOffset(reference.Date, reference.Offset),
                ReflectionRangeFilter.Last7Days => reference.AddDays(-7),
                ReflectionRangeFilter.Last30Days => reference.AddDays(-30),
                _ => DateTimeOffset.MinValue
            };

            query = query.Where(r => r.At.ToLocalTime() >= cutoff);
        }

        IOrderedEnumerable<ReflectionRunSummary> ordered = sortKey switch
        {
            ReflectionSortKey.Suggestions => Order(query, r => r.Suggestions.Count, descending),
            ReflectionSortKey.Skills => Order(query, r => r.CreatedSkills.Count, descending),
            ReflectionSortKey.Tokens => Order(query, r => r.TotalTokens, descending),
            _ => Order(query, r => r.At, descending)
        };

        return ordered.ToList();
    }

    /// <summary>
    /// 排序。数量列用「时间倒序」做次序键，保证同值行的相对顺序稳定
    /// （否则每次重排都会让同数记录跳动）。
    /// </summary>
    private static IOrderedEnumerable<ReflectionRunSummary> Order<TKey>(
        IEnumerable<ReflectionRunSummary> query,
        Func<ReflectionRunSummary, TKey> selector,
        bool descending)
        => descending
            ? query.OrderByDescending(selector).ThenByDescending(r => r.At)
            : query.OrderBy(selector).ThenByDescending(r => r.At);

    private static bool Contains(string? value, string term)
        => !string.IsNullOrEmpty(value)
           && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
