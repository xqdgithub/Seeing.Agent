using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆管理核心服务，提供 CRUD 操作和检索功能。
/// 作为服务层，调用 Repository 和 Retriever 方法，不实现存储逻辑。
/// </summary>
public class MemoryManager : IMemoryManager
{
    private readonly IMemoryRepository _repository;
    private readonly IMemoryRetriever _retriever;
    private readonly ILogger<MemoryManager>? _logger;

    /// <summary>
    /// 创建 MemoryManager 实例
    /// </summary>
    /// <param name="repository">记忆存储仓库</param>
    /// <param name="retriever">记忆检索器</param>
    /// <param name="logger">日志记录器</param>
    public MemoryManager(
        IMemoryRepository repository,
        IMemoryRetriever retriever,
        ILogger<MemoryManager>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _logger = logger;
    }

    /// <summary>
    /// 初始化记忆管理系统
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger?.LogInformation("初始化记忆管理系统");
        // 当前版本无需额外初始化，Repository 在构造时已确保目录存在
        await Task.CompletedTask;
    }

    /// <summary>
    /// 创建新的记忆条目
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <returns>创建成功的记忆 ID</returns>
    public async Task<string> CreateMemoryAsync(MemoryEntry memory)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        // 验证记忆 ID
        if (string.IsNullOrWhiteSpace(memory.Id))
        {
            throw new ArgumentException("记忆 ID 不能为空", nameof(memory));
        }

        // 验证必要字段
        if (memory.Metadata == null)
        {
            throw new ArgumentException("记忆 Metadata 不能为空", nameof(memory));
        }

        if (string.IsNullOrWhiteSpace(memory.Metadata.SessionId))
        {
            throw new ArgumentException("SessionId 不能为空", nameof(memory));
        }

        // 调用 Repository 保存记忆
        await _repository.SaveMemoryAsync(memory);

        _logger?.LogDebug("创建记忆成功: {MemoryId}, 类型: {Type}", memory.Id, memory.Type);
        return memory.Id;
    }

    /// <summary>
    /// 根据 ID 获取记忆条目
    /// </summary>
    /// <param name="id">记忆 ID</param>
    /// <returns>记忆条目，不存在时返回 null</returns>
    public async Task<MemoryEntry?> GetMemoryAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("记忆 ID 不能为空", nameof(id));
        }

        // 调用 Repository 获取记忆
        var memory = await _repository.GetMemoryAsync(id);

        if (memory == null)
        {
            _logger?.LogDebug("记忆不存在: {MemoryId}", id);
            return null;
        }

        var entry = memory as MemoryEntry;
        if (entry == null)
        {
            _logger?.LogWarning("记忆类型转换失败: {MemoryId}", id);
            return null;
        }

        _logger?.LogDebug("获取记忆成功: {MemoryId}", id);
        return entry;
    }

    /// <summary>
    /// 更新记忆条目（部分更新）
    /// </summary>
    /// <param name="id">记忆 ID</param>
    /// <param name="update">更新数据</param>
    /// <returns>更新后的记忆条目</returns>
    public async Task<MemoryEntry> UpdateMemoryAsync(string id, MemoryUpdate update)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("记忆 ID 不能为空", nameof(id));
        }

        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        // 获取现有记忆
        var existingMemory = await GetMemoryAsync(id);
        if (existingMemory == null)
        {
            throw new InvalidOperationException($"记忆不存在: {id}");
        }

        // 应用更新（创建新记录，保留原有字段，更新指定字段）
        var updatedEntry = ApplyUpdate(existingMemory, update);

        // 调用 Repository 保存更新后的记忆
        await _repository.SaveMemoryAsync(updatedEntry);

        _logger?.LogDebug("更新记忆成功: {MemoryId}", id);
        return updatedEntry;
    }

    /// <summary>
    /// 删除记忆条目
    /// </summary>
    /// <param name="id">记忆 ID</param>
    public async Task DeleteMemoryAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("记忆 ID 不能为空", nameof(id));
        }

        // 调用 Repository 删除记忆
        await _repository.DeleteMemoryAsync(id);

        _logger?.LogDebug("删除记忆成功: {MemoryId}", id);
    }

    /// <summary>
    /// 搜索记忆（基于查询字符串和过滤条件）
    /// </summary>
    /// <param name="query">查询字符串</param>
    /// <param name="filter">过滤条件</param>
    /// <returns>搜索结果</returns>
    public async Task<MemorySearchResult> SearchMemoriesAsync(string query, MemoryFilter? filter = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("查询字符串不能为空", nameof(query));
        }

        // 调用 Retriever 检索记忆
        var memories = await _retriever.RetrieveAsync(query);
        var entries = memories.OfType<MemoryEntry>().ToList();

        // 如果有过滤条件，应用过滤
        if (filter != null)
        {
            entries = entries.Where(e => MatchesFilter(e, filter)).ToList();
        }

        _logger?.LogDebug("搜索记忆完成，查询: {Query}, 结果数量: {Count}", query, entries.Count);
        return new MemorySearchResult(entries, entries.Count);
    }

    /// <summary>
    /// 列出所有记忆（保留兼容性）
    /// </summary>
    /// <returns>所有记忆条目</returns>
    public async Task<IEnumerable<object>> ListMemoriesAsync()
    {
        var memories = await _repository.ListMemoriesAsync();
        _logger?.LogDebug("列出所有记忆，数量: {Count}", memories.Count());
        return memories;
    }

    /// <summary>
    /// 应用更新到现有记忆条目（创建新记录）
    /// </summary>
    /// <param name="existing">现有记忆条目</param>
    /// <param name="update">更新数据</param>
    /// <returns>更新后的记忆条目</returns>
    private static MemoryEntry ApplyUpdate(MemoryEntry existing, MemoryUpdate update)
    {
        // 更新 Metadata（保留原有字段，更新指定字段）
        var updatedMetadata = new MemoryMetadata(
            existing.Metadata.SessionId, // SessionId 不可更新
            existing.Metadata.AgentId,    // AgentId 不可更新
            update.Source ?? existing.Metadata.Source,
            update.Tags ?? existing.Metadata.Tags,
            update.Confidence ?? existing.Metadata.Confidence,
            update.Importance ?? existing.Metadata.Importance,
            existing.Metadata.AccessCount + (update.AccessCountDelta ?? 0)
        );

        // 创建更新后的记忆条目
        return new MemoryEntry(
            existing.Id,
            existing.Type,
            update.Content ?? existing.Content,
            updatedMetadata,
            existing.CreatedAt,
            update.ValidAt ?? existing.ValidAt,
            update.InvalidAt ?? existing.InvalidAt
        );
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

        // Tags 过滤
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