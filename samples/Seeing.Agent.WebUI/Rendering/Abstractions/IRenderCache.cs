namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 渲染缓存接口
/// </summary>
public interface IRenderCache
{
    /// <summary>
    /// 获取或创建 Markdown 渲染缓存
    /// </summary>
    /// <param name="content">原始内容</param>
    /// <param name="cacheKey">缓存键</param>
    /// <returns>渲染后的 HTML</returns>
    string GetOrCreateMarkdown(string content, string cacheKey);

    /// <summary>
    /// 尝试获取缓存
    /// </summary>
    bool TryGet(string cacheKey, out string? value);

    /// <summary>
    /// 设置缓存
    /// </summary>
    void Set(string cacheKey, string content, string rendered);

    /// <summary>
    /// 使缓存失效
    /// </summary>
    void Invalidate(string cacheKey);

    /// <summary>
    /// 使消息相关的所有缓存失效
    /// </summary>
    void InvalidateMessage(string messageId);

    /// <summary>
    /// 清空所有缓存
    /// </summary>
    void Clear();

    /// <summary>
    /// 获取缓存统计
    /// </summary>
    CacheStatistics GetStatistics();
}

/// <summary>
/// 缓存统计信息
/// </summary>
public record CacheStatistics
{
    /// <summary>
    /// 缓存项数量
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// 总大小（字节）
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// 命中次数
    /// </summary>
    public long HitCount { get; init; }

    /// <summary>
    /// 未命中次数
    /// </summary>
    public long MissCount { get; init; }

    /// <summary>
    /// 驱逐次数（LRU 清理）
    /// </summary>
    public long EvictedCount { get; init; }

    /// <summary>
    /// 命中率
    /// </summary>
    public double HitRate => HitCount + MissCount > 0
        ? (double)HitCount / (HitCount + MissCount)
        : 0;
}
