using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 重复记忆检测结果。
/// </summary>
public class DuplicateGroup
{
    /// <summary>
    /// 重复组的唯一标识。
    /// </summary>
    public string GroupId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 组内所有重复的记忆条目。
    /// </summary>
    public List<MemoryEntry> Entries { get; set; } = new();

    /// <summary>
    /// 组内最大相似度分数。
    /// </summary>
    public double MaxSimilarity { get; set; }

    /// <summary>
    /// 推荐保留的记忆条目（通常是最早创建或重要性最高的）。
    /// </summary>
    public MemoryEntry? RecommendedPrimary { get; set; }
}

/// <summary>
/// 合并重复记忆的结果。
/// </summary>
public class MergeResult
{
    /// <summary>
    /// 是否成功合并。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 新创建的合并记忆 ID（如果有）。
    /// </summary>
    public string? MergedMemoryId { get; set; }

    /// <summary>
    /// 保留的原始记忆数量。
    /// </summary>
    public int RetainedCount { get; set; }

    /// <summary>
    /// 合并过程中的消息或错误描述。
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Memory 去重合并器。
/// 基于内容相似度检测重复记忆，并支持合并操作。
/// V1 实现使用文本相似度，预留向量相似度扩展接口。
/// </summary>
public class MemoryDeduplicator
{
    private readonly ISimilarityChecker _similarityChecker;
    private readonly IMemoryRepository _repository;
    private readonly ILogger<MemoryDeduplicator>? _logger;
    private readonly DeduplicatorOptions _options;

    /// <summary>
    /// 创建 MemoryDeduplicator 实例。
    /// </summary>
    /// <param name="similarityChecker">相似度检查器</param>
    /// <param name="repository">记忆存储</param>
    /// <param name="options">去重配置选项</param>
    /// <param name="logger">日志记录器</param>
    public MemoryDeduplicator(
        ISimilarityChecker similarityChecker,
        IMemoryRepository repository,
        DeduplicatorOptions? options = null,
        ILogger<MemoryDeduplicator>? logger = null)
    {
        _similarityChecker = similarityChecker ?? throw new ArgumentNullException(nameof(similarityChecker));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? new DeduplicatorOptions();
        _logger = logger;
    }

    /// <summary>
    /// 查找指定会话中的重复记忆。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="threshold">相似度阈值，默认 0.8</param>
    /// <returns>重复记忆组列表</returns>
    public async Task<List<DuplicateGroup>> FindDuplicatesAsync(string sessionId, double threshold = 0.8)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
        }

        _logger?.LogInformation("开始查找会话 {SessionId} 的重复记忆，阈值: {Threshold}", sessionId, threshold);

        // 获取所有记忆
        var allMemories = await _repository.ListMemoriesAsync();
        var memories = allMemories
            .Cast<MemoryEntry>()
            .Where(m => m.Metadata?.SessionId == sessionId)
            .ToList();

        if (memories.Count == 0)
        {
            _logger?.LogInformation("会话 {SessionId} 没有找到任何记忆", sessionId);
            return new List<DuplicateGroup>();
        }

        _logger?.LogDebug("会话 {SessionId} 共有 {Count} 条记忆待检查", sessionId, memories.Count);

        // 查找重复组
        var duplicateGroups = new List<DuplicateGroup>();
        var processedIds = new HashSet<string>();

        for (var i = 0; i < memories.Count; i++)
        {
            var entry1 = memories[i];

            if (processedIds.Contains(entry1.Id))
            {
                continue;
            }

            var duplicatesInGroup = new List<MemoryEntry> { entry1 };
            var maxSimilarity = 0.0;

            for (var j = i + 1; j < memories.Count; j++)
            {
                var entry2 = memories[j];

                if (processedIds.Contains(entry2.Id))
                {
                    continue;
                }

                var similarity = await _similarityChecker.CalculateSimilarityAsync(entry1, entry2);

                if (similarity >= threshold)
                {
                    duplicatesInGroup.Add(entry2);
                    processedIds.Add(entry2.Id);
                    maxSimilarity = Math.Max(maxSimilarity, similarity);

                    _logger?.LogDebug(
                        "发现重复: {Id1} <-> {Id2}, 相似度: {Similarity:F2}",
                        entry1.Id, entry2.Id, similarity);
                }
            }

            // 只保留真正有重复的组（至少 2 条记忆）
            if (duplicatesInGroup.Count >= 2)
            {
                processedIds.Add(entry1.Id);

                var group = new DuplicateGroup
                {
                    GroupId = Guid.NewGuid().ToString(),
                    Entries = duplicatesInGroup,
                    MaxSimilarity = maxSimilarity,
                    RecommendedPrimary = SelectPrimaryEntry(duplicatesInGroup)
                };

                duplicateGroups.Add(group);

                _logger?.LogInformation(
                    "发现重复组: {GroupId}, 包含 {Count} 条记忆, 最大相似度: {Similarity:F2}",
                    group.GroupId, group.Entries.Count, group.MaxSimilarity);
            }
        }

        _logger?.LogInformation(
            "会话 {SessionId} 重复检测完成: 共 {Total} 条记忆, 发现 {Groups} 个重复组",
            sessionId, memories.Count, duplicateGroups.Count);

        return duplicateGroups;
    }

    /// <summary>
    /// 合并重复记忆。
    /// 使用 ADD-only 策略：保留原始记忆，可选创建合并摘要。
    /// </summary>
    /// <param name="duplicates">重复记忆组</param>
    /// <param name="createSummary">是否创建合并摘要记忆，默认 false</param>
    /// <returns>合并结果</returns>
    public async Task<MergeResult> MergeDuplicatesAsync(List<MemoryEntry> duplicates, bool createSummary = false)
    {
        if (duplicates == null || duplicates.Count == 0)
        {
            return new MergeResult
            {
                Success = false,
                Message = "重复记忆列表为空"
            };
        }

        if (duplicates.Count < 2)
        {
            return new MergeResult
            {
                Success = false,
                Message = "需要至少 2 条记忆才能合并"
            };
        }

        _logger?.LogInformation("开始合并 {Count} 条重复记忆", duplicates.Count);

        // ADD-only 策略：保留所有原始记忆
        // 如果 createSummary=true，创建一条新的合并摘要记忆
        var result = new MergeResult
        {
            Success = true,
            RetainedCount = duplicates.Count,
            Message = "ADD-only 策略：保留所有原始记忆"
        };

        if (createSummary)
        {
            // 选择主要记忆作为合并摘要的基础
            var primary = SelectPrimaryEntry(duplicates);

            // 创建合并摘要记忆
            var summaryContent = BuildSummaryContent(duplicates);
            var mergedTags = MergeTags(duplicates);
            var maxImportance = duplicates.Max(d => d.Metadata?.Importance ?? 0.5);
            var maxConfidence = duplicates.Max(d => d.Metadata?.Confidence ?? 0.5);

            var mergedEntry = new MemoryEntry(
                Id: $"merged_{Guid.NewGuid():N}",
                Type: primary.Type,
                Content: summaryContent,
                Metadata: new MemoryMetadata(
                    SessionId: primary.Metadata?.SessionId ?? string.Empty,
                    AgentId: primary.Metadata?.AgentId ?? string.Empty,
                    Source: "deduplicator_merge",
                    Tags: mergedTags,
                    Confidence: maxConfidence,
                    Importance: maxImportance
                ),
                CreatedAt: DateTimeOffset.UtcNow,
                ValidAt: DateTimeOffset.UtcNow,
                InvalidAt: null
            );

            await _repository.SaveMemoryAsync(mergedEntry);
            result.MergedMemoryId = mergedEntry.Id;
            result.Message = $"ADD-only 策略：保留 {duplicates.Count} 条原始记忆，创建 1 条合并摘要";

            _logger?.LogInformation(
                "合并摘要已创建: {MergedId}, 基于 {Count} 条重复记忆",
                mergedEntry.Id, duplicates.Count);
        }

        return result;
    }

    /// <summary>
    /// 选择主要记忆条目（最早创建或重要性最高的）。
    /// </summary>
    private MemoryEntry SelectPrimaryEntry(List<MemoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("entries 不能为空", nameof(entries));
        }

        // 按重要性排序，其次按创建时间（ValidAt）
        return entries
            .OrderByDescending(e => e.Metadata?.Importance ?? 0.5)
            .ThenBy(e => e.ValidAt)
            .First();
    }

    /// <summary>
    /// 构建合并摘要内容。
    /// </summary>
    private string BuildSummaryContent(List<MemoryEntry> entries)
    {
        var primary = SelectPrimaryEntry(entries);
        var otherIds = entries.Where(e => e.Id != primary.Id).Select(e => e.Id).ToList();

        var summary = $"**合并摘要**（基于 {primary.Id}）\n\n" +
                      $"{primary.Content}\n\n" +
                      $"---\n" +
                      $"合并来源: {string.Join(", ", otherIds)}\n" +
                      $"合并时间: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}";

        return summary;
    }

    /// <summary>
    /// 合并所有记忆的标签。
    /// </summary>
    private IReadOnlyList<string> MergeTags(List<MemoryEntry> entries)
    {
        var allTags = new HashSet<string>();

        foreach (var entry in entries)
        {
            if (entry.Metadata?.Tags != null)
            {
                foreach (var tag in entry.Metadata.Tags)
                {
                    allTags.Add(tag);
                }
            }
        }

        // 添加合并标记
        allTags.Add("merged");

        return allTags.ToList();
    }
}

/// <summary>
/// 去重合并器配置选项。
/// </summary>
public class DeduplicatorOptions
{
    /// <summary>
    /// 默认相似度阈值。默认 0.8。
    /// </summary>
    public double DefaultThreshold { get; set; } = 0.8;

    /// <summary>
    /// 是否默认创建合并摘要。默认 false（ADD-only 策略）。
    /// </summary>
    public bool CreateSummaryByDefault { get; set; } = false;

    /// <summary>
    /// 最大处理的记忆数量。默认 1000。
    /// </summary>
    public int MaxProcessCount { get; set; } = 1000;

    /// <summary>
    /// 并行检查的最大并发数。默认 4。
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;
}