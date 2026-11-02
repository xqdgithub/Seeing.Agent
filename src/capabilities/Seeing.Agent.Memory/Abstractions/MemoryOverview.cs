using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>某一天的记忆条数。</summary>
public record MemoryDailyCount(DateOnly Date, int Count);

/// <summary>
/// 记忆总览：一次加载得出的全部聚合。
/// <para>
/// <see cref="DailyCounts"/> 长度恒为请求的 <c>days</c>（含 0 值日期），窗口外的记忆
/// 不计入它，但仍计入 <see cref="TotalCount"/> 与 <see cref="CountByType"/>——两者语义不同。
/// </para>
/// </summary>
public record MemoryOverview(
    int TotalCount,
    IReadOnlyDictionary<MemoryType, int> CountByType,
    IReadOnlyList<MemoryDailyCount> DailyCounts,
    IndexStats Index,
    GraphStats Graph);
