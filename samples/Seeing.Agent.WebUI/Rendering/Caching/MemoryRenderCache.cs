using Markdig;
using Seeing.Agent.WebUI.Rendering.Abstractions;
using System.Collections.Concurrent;

namespace Seeing.Agent.WebUI.Rendering.Caching;

/// <summary>
/// 内存渲染缓存实现
/// </summary>
/// <remarks>
/// <para>
/// 此实现使用不可变缓存条目 + 原子更新策略，确保线程安全：
/// <list type="bullet">
///   <item><description>使用 ConcurrentDictionary.AddOrUpdate 保证原子性</description></item>
///   <item><description>缓存条目为不可变 record，避免部分更新</description></item>
///   <item><description>LRU 策略防止内存无限增长</description></item>
/// </list>
/// </para>
/// </remarks>
public class MemoryRenderCache : IRenderCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly MarkdownPipeline _markdownPipeline;
    private readonly ILogger<MemoryRenderCache> _logger;

    // 统计信息
    private long _hitCount;
    private long _missCount;

    // LRU 配置
    private const int MaxCacheSize = 5000;
    private long _evictedCount;

    public MemoryRenderCache(ILogger<MemoryRenderCache> logger)
    {
        _logger = logger;

        // 配置 Markdown 管道（支持完整 Markdown 语法，禁用原始 HTML 透传）
        _markdownPipeline = new MarkdownPipelineBuilder()
            .DisableHtml()       // 禁止原始 HTML 透传，防止非 Markdown 内容被直接渲染
            .UseAutoLinks()
            .UseTaskLists()
            .UsePipeTables()
            .UseGridTables()
            .UseListExtras()
            .UseEmphasisExtras()
            .UseAutoIdentifiers()
            .UseCitations()
            .UseCustomContainers()
            .UseFooters()
            .UseFootnotes()
            .UseMathematics()
            .UseSmartyPants()
            .Build();
    }

    /// <summary>
    /// 获取或创建 Markdown 渲染缓存（线程安全）
    /// </summary>
    /// <remarks>
    /// 使用 AddOrUpdate 保证检查和更新的原子性，避免竞态条件。
    /// </remarks>
    public string GetOrCreateMarkdown(string content, string cacheKey)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var now = DateTime.UtcNow;

        // 使用 AddOrUpdate 保证原子性
        var entry = _cache.AddOrUpdate(
            cacheKey,
            // 新建条目
            key =>
            {
                Interlocked.Increment(ref _missCount);
                TrimIfNeeded();
                return new CacheEntry(
                    content,
                    Markdown.ToHtml(content, _markdownPipeline),
                    now,
                    null
                );
            },
            // 更新现有条目（仅当内容变化时）
            (key, existing) =>
            {
                if (existing.Content == content)
                {
                    // 内容未变化，命中缓存
                    Interlocked.Increment(ref _hitCount);
                    return existing; // 返回原条目，不创建新对象
                }

                // 内容变化，更新缓存
                Interlocked.Increment(ref _missCount);
                return new CacheEntry(
                    content,
                    Markdown.ToHtml(content, _markdownPipeline),
                    existing.CreatedAt, // 保留创建时间
                    now                  // 更新时间
                );
            }
        );

        return entry.RenderedHtml;
    }

    public bool TryGet(string cacheKey, out string? value)
    {
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            Interlocked.Increment(ref _hitCount);
            value = entry.RenderedHtml;
            return true;
        }

        Interlocked.Increment(ref _missCount);
        value = null;
        return false;
    }

    public void Set(string cacheKey, string content, string rendered)
    {
        var now = DateTime.UtcNow;
        TrimIfNeeded();

        _cache[cacheKey] = new CacheEntry(content, rendered, now, now);
    }

    public void Invalidate(string cacheKey)
    {
        if (_cache.TryRemove(cacheKey, out _))
        {
            _logger.LogDebug("Cache invalidated: {CacheKey}", cacheKey);
        }
    }

    public void InvalidateMessage(string messageId)
    {
        // 移除与该消息相关的所有缓存（线程安全枚举）
        var removedCount = 0;
        foreach (var key in _cache.Keys)
        {
            if (key.Contains(messageId) && _cache.TryRemove(key, out _))
            {
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _logger.LogDebug("Invalidated {Count} cache entries for message {MessageId}",
                removedCount, messageId);
        }
    }

    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("Cache cleared, {Count} entries removed", count);
    }

    public CacheStatistics GetStatistics()
    {
        // 使用 ToArray 创建快照，避免枚举期间集合被修改
        var snapshot = _cache.ToArray();

        return new CacheStatistics
        {
            Count = snapshot.Length,
            TotalSize = snapshot.Sum(kvp => kvp.Value.Content.Length + kvp.Value.RenderedHtml.Length),
            HitCount = Interlocked.Read(ref _hitCount),
            MissCount = Interlocked.Read(ref _missCount),
            EvictedCount = Interlocked.Read(ref _evictedCount)
        };
    }

    /// <summary>
    /// LRU 清理：当缓存超过最大大小时，移除最旧的条目
    /// </summary>
    private void TrimIfNeeded()
    {
        if (_cache.Count <= MaxCacheSize)
            return;

        // 计算需要移除的数量
        var toRemoveCount = _cache.Count - (int)(MaxCacheSize * 0.8); // 清理到 80%

        if (toRemoveCount <= 0)
            return;

        // 找出最旧的条目（按 UpdatedAt 降序，保留最近使用的）
        var keysToRemove = _cache
            .OrderBy(kvp => kvp.Value.UpdatedAt ?? kvp.Value.CreatedAt)
            .Take(toRemoveCount)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_cache.TryRemove(key, out _))
            {
                Interlocked.Increment(ref _evictedCount);
            }
        }

        if (keysToRemove.Count > 0)
        {
            _logger.LogDebug("LRU cache trim: removed {Count} entries, current size: {Current}",
                keysToRemove.Count, _cache.Count);
        }
    }

    /// <summary>
    /// 不可变缓存条目（使用 record 保证不可变性）
    /// </summary>
    private record CacheEntry(
        string Content,
        string RenderedHtml,
        DateTime CreatedAt,
        DateTime? UpdatedAt
    );
}
