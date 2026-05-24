using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆检索器实现，支持元数据过滤、时间范围查询和有效性过滤。
/// </summary>
public class MemoryRetriever : IMemoryRetriever
{
    private readonly IMemoryRepository _repository;
    private readonly ILogger<MemoryRetriever>? _logger;

    /// <summary>
    /// 创建 MemoryRetriever 实例
    /// </summary>
    /// <param name="repository">记忆存储仓库</param>
    /// <param name="logger">日志记录器</param>
    public MemoryRetriever(IMemoryRepository repository, ILogger<MemoryRetriever>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
    }

    /// <summary>
    /// 根据查询字符串检索记忆（通用方法）
    /// </summary>
    /// <param name="query">查询字符串</param>
    /// <returns>匹配的记忆条目</returns>
    public async Task<IEnumerable<object>> RetrieveAsync(string query)
    {
        // 基础实现：返回所有记忆，后续可扩展为全文搜索
        _logger?.LogDebug("执行查询: {Query}", query);
        var memories = await _repository.ListMemoriesAsync();
        return memories;
    }

    /// <summary>
    /// 根据条件对象检索记忆（通用方法）
    /// </summary>
    /// <param name="criteria">查询条件</param>
    /// <returns>匹配的记忆条目</returns>
    public async Task<IEnumerable<object>> RetrieveAsync(object criteria)
    {
        if (criteria is MemoryFilter filter)
        {
            var result = await RetrieveByMetadataAsync(filter);
            return result;
        }

        _logger?.LogWarning("不支持的查询条件类型: {CriteriaType}", criteria?.GetType().Name);
        return Enumerable.Empty<object>();
    }

    /// <summary>
    /// 根据元数据过滤条件检索记忆
    /// </summary>
    /// <param name="filter">记忆过滤条件</param>
    /// <returns>匹配的记忆条目列表</returns>
    public async Task<IReadOnlyList<MemoryEntry>> RetrieveByMetadataAsync(MemoryFilter filter)
    {
        if (filter == null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        var allMemories = await _repository.ListMemoriesAsync();
        var entries = allMemories.OfType<MemoryEntry>();

        var results = new List<MemoryEntry>();

        foreach (var entry in entries)
        {
            if (MatchesFilter(entry, filter))
            {
                results.Add(entry);
            }
        }

        _logger?.LogDebug("元数据过滤查询完成，条件: {Filter}，结果数量: {Count}", filter, results.Count);
        return results;
    }

    /// <summary>
    /// 根据时间范围检索记忆（基于 ValidAt 字段）
    /// </summary>
    /// <param name="from">起始时间</param>
    /// <param name="to">结束时间</param>
    /// <returns>时间范围内的记忆条目列表</returns>
    public async Task<IReadOnlyList<MemoryEntry>> RetrieveByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to)
    {
        if (from > to)
        {
            throw new ArgumentException("起始时间不能晚于结束时间", nameof(from));
        }

        var allMemories = await _repository.ListMemoriesAsync();
        var entries = allMemories.OfType<MemoryEntry>();

        var results = entries
            .Where(e => e.ValidAt >= from && e.ValidAt <= to)
            .OrderBy(e => e.ValidAt)
            .ToList();

        _logger?.LogDebug(
            "时间范围查询完成，范围: [{From}, {To}]，结果数量: {Count}",
            from, to, results.Count);

        return results;
    }

    /// <summary>
    /// 根据 Agent ID 检索记忆
    /// </summary>
    /// <param name="agentId">Agent 标识符</param>
    /// <returns>该 Agent 创建的记忆条目列表</returns>
    public async Task<IReadOnlyList<MemoryEntry>> RetrieveByAgentAsync(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent ID 不能为空", nameof(agentId));
        }

        var allMemories = await _repository.ListMemoriesAsync();
        var entries = allMemories.OfType<MemoryEntry>();

        var results = entries
            .Where(e => string.Equals(e.Metadata.AgentId, agentId, StringComparison.Ordinal))
            .OrderByDescending(e => e.ValidAt)
            .ToList();

        _logger?.LogDebug("Agent 查询完成，AgentId: {AgentId}，结果数量: {Count}", agentId, results.Count);
        return results;
    }

    /// <summary>
    /// 检索当前有效的记忆（valid_at &lt;= now &lt; invalid_at）
    /// </summary>
    /// <param name="now">当前时间点</param>
    /// <returns>当前有效的记忆条目列表</returns>
    public async Task<IReadOnlyList<MemoryEntry>> RetrieveValidAsync(DateTimeOffset now)
    {
        var allMemories = await _repository.ListMemoriesAsync();
        var entries = allMemories.OfType<MemoryEntry>();

        var results = entries
            .Where(e => IsValidAt(e, now))
            .OrderByDescending(e => e.ValidAt)
            .ToList();

        _logger?.LogDebug("有效性查询完成，时间点: {Now}，结果数量: {Count}", now, results.Count);
        return results;
    }

    /// <summary>
    /// 检查记忆条目是否在指定时间点有效
    /// </summary>
    /// <param name="entry">记忆条目</param>
    /// <param name="now">当前时间点</param>
    /// <returns>是否有效</returns>
    private static bool IsValidAt(MemoryEntry entry, DateTimeOffset now)
    {
        // valid_at <= now
        if (entry.ValidAt > now)
        {
            return false;
        }

        // 如果没有 invalid_at，则永久有效
        if (entry.InvalidAt == null)
        {
            return true;
        }

        // now < invalid_at
        return now < entry.InvalidAt.Value;
    }

    /// <summary>
    /// 检查记忆条目是否匹配过滤条件
    /// </summary>
    /// <param name="entry">记忆条目</param>
    /// <param name="filter">过滤条件</param>
    /// <returns>是否匹配</returns>
    private static bool MatchesFilter(MemoryEntry entry, MemoryFilter filter)
    {
        // 类型过滤
        if (filter.Type.HasValue && entry.Type != filter.Type.Value)
        {
            return false;
        }

        // SessionId 过滤
        if (!string.IsNullOrEmpty(filter.SessionId) &&
            !string.Equals(entry.Metadata.SessionId, filter.SessionId, StringComparison.Ordinal))
        {
            return false;
        }

        // AgentId 过滤
        if (!string.IsNullOrEmpty(filter.AgentId) &&
            !string.Equals(entry.Metadata.AgentId, filter.AgentId, StringComparison.Ordinal))
        {
            return false;
        }

        // Source 过滤
        if (!string.IsNullOrEmpty(filter.Source) &&
            !string.Equals(entry.Metadata.Source, filter.Source, StringComparison.Ordinal))
        {
            return false;
        }

        // Tags 过滤（包含任意一个标签即匹配）
        if (filter.Tags != null && filter.Tags.Count > 0)
        {
            if (entry.Metadata.Tags == null || entry.Metadata.Tags.Count == 0)
            {
                return false;
            }

            var hasMatchingTag = filter.Tags.Any(filterTag =>
                entry.Metadata.Tags.Any(entryTag =>
                    string.Equals(entryTag, filterTag, StringComparison.OrdinalIgnoreCase)));

            if (!hasMatchingTag)
            {
                return false;
            }
        }

        // ValidAt 时间范围过滤
        if (filter.ValidAtFrom.HasValue && entry.ValidAt < filter.ValidAtFrom.Value)
        {
            return false;
        }

        if (filter.ValidAtTo.HasValue && entry.ValidAt > filter.ValidAtTo.Value)
        {
            return false;
        }

        // InvalidAt 时间范围过滤
        if (entry.InvalidAt.HasValue)
        {
            if (filter.InvalidAtFrom.HasValue && entry.InvalidAt.Value < filter.InvalidAtFrom.Value)
            {
                return false;
            }

            if (filter.InvalidAtTo.HasValue && entry.InvalidAt.Value > filter.InvalidAtTo.Value)
            {
                return false;
            }
        }

        return true;
    }
}