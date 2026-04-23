using System;
using System.Collections.Concurrent;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 多因子记忆评分器，实现业界标准的遗忘机制。
/// 评分公式: score = α×importance + β×access_freq - γ×age
/// </summary>
public class MemoryScorer : IMemoryScorer
{
    private readonly MemoryScoreOptions _options;
    private readonly ConcurrentDictionary<string, int> _accessCounts = new();

    /// <summary>
    /// 初始化 MemoryScorer
    /// </summary>
    /// <param name="options">评分配置选项</param>
    public MemoryScorer(MemoryScoreOptions? options = null)
    {
        _options = options ?? new MemoryScoreOptions();
    }

    /// <summary>
    /// 计算记忆条目的综合评分
    /// </summary>
    /// <param name="memory">记忆条目</param>
    /// <param name="now">当前时间</param>
    /// <returns>综合评分（越高越重要）</returns>
    public double CalculateScore(MemoryEntry memory, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(memory);

        // α × importance
        double importanceScore = _options.ImportanceWeight * memory.Metadata.Importance;

        // β × access_freq
        int accessCount = GetAccessCount(memory.Id, memory.Metadata.AccessCount);
        double accessFreqScore = _options.AccessFreqWeight * Math.Log10(accessCount + 1);

        // γ × age (天数)
        double ageInDays = (now - memory.CreatedAt.DateTime).TotalDays;
        double ageScore = _options.AgeWeight * ageInDays;

        // score = α×importance + β×access_freq - γ×age
        return importanceScore + accessFreqScore - ageScore;
    }

    /// <summary>
    /// 更新指定记忆条目的访问计数
    /// </summary>
    /// <param name="memoryId">记忆条目 ID</param>
    /// <returns>更新后的访问计数</returns>
    public int UpdateAccessCount(string memoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);

        return _accessCounts.AddOrUpdate(memoryId, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// 获取指定记忆条目的访问计数
    /// </summary>
    /// <param name="memoryId">记忆条目 ID</param>
    /// <param name="defaultValue">默认值（从元数据中读取）</param>
    /// <returns>当前访问计数</returns>
    public int GetAccessCount(string memoryId, int defaultValue = 0)
    {
        return _accessCounts.GetOrAdd(memoryId, defaultValue);
    }

    /// <summary>
    /// 判断记忆条目是否应该被遗忘
    /// </summary>
    /// <param name="score">记忆评分</param>
    /// <param name="threshold">遗忘阈值（默认使用配置值）</param>
    /// <returns>true 表示应该遗忘，false 表示保留</returns>
    public bool ShouldForget(double score, double? threshold = null)
    {
        double effectiveThreshold = threshold ?? _options.ForgettingThreshold;
        return score < effectiveThreshold;
    }

    /// <summary>
    /// 批量计算评分并排序
    /// </summary>
    /// <param name="memories">记忆条目集合</param>
    /// <param name="now">当前时间</param>
    /// <returns>按评分降序排列的记忆条目</returns>
    public IEnumerable<(MemoryEntry Memory, double Score)> RankMemories(
        IEnumerable<MemoryEntry> memories,
        DateTime now)
    {
        return memories
            .Select(m => (Memory: m, Score: CalculateScore(m, now)))
            .OrderByDescending(x => x.Score);
    }

    /// <summary>
    /// 筛选需要遗忘的记忆条目
    /// </summary>
    /// <param name="memories">记忆条目集合</param>
    /// <param name="now">当前时间</param>
    /// <param name="threshold">遗忘阈值</param>
    /// <returns>应该被遗忘的记忆条目</returns>
    public IEnumerable<MemoryEntry> GetMemoriesToForget(
        IEnumerable<MemoryEntry> memories,
        DateTime now,
        double? threshold = null)
    {
        return memories.Where(m => ShouldForget(CalculateScore(m, now), threshold));
    }

    #region IMemoryScorer Implementation

    /// <summary>
    /// 异步评分接口实现
    /// </summary>
    public Task<double> ScoreAsync(object memory, IDictionary<string, object> options)
    {
        if (memory is not MemoryEntry entry)
        {
            throw new ArgumentException("memory must be of type MemoryEntry", nameof(memory));
        }

        DateTime now = options.TryGetValue("now", out var nowObj) && nowObj is DateTime dt
            ? dt
            : DateTime.UtcNow;

        return Task.FromResult(CalculateScore(entry, now));
    }

    #endregion
}