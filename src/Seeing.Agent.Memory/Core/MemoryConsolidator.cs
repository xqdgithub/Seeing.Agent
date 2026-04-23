using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆合并结果。
/// </summary>
public class ConsolidationResult
{
    /// <summary>
    /// 是否成功合并。
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 新创建的合并记忆 ID。
    /// </summary>
    public string? ConsolidatedMemoryId { get; set; }

    /// <summary>
    /// 参与合并的原始记忆数量。
    /// </summary>
    public int SourceCount { get; set; }

    /// <summary>
    /// 保留的原始记忆数量（ADD-only 策略下等于 SourceCount）。
    /// </summary>
    public int RetainedCount { get; set; }

    /// <summary>
    /// 合并过程中的消息或错误描述。
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// 合并时间范围（最早到最晚）。
    /// </summary>
    public (DateTimeOffset Earliest, DateTimeOffset Latest)? TimeRange { get; set; }
}

/// <summary>
/// 记忆合并配置选项。
/// </summary>
public class ConsolidatorOptions
{
    /// <summary>
    /// 合并内容摘要的最大长度（字符数）。默认 4000。
    /// </summary>
    public int MaxSummaryLength { get; set; } = 4000;

    /// <summary>
    /// 是否在合并摘要中包含时间信息。默认 true。
    /// </summary>
    public bool IncludeTimestamps { get; set; } = true;

    /// <summary>
    /// 是否在合并摘要中包含来源 ID。默认 true。
    /// </summary>
    public bool IncludeSourceIds { get; set; } = true;

    /// <summary>
    /// 合并后添加的标签。默认 ["consolidated"]。
    /// </summary>
    public List<string> ConsolidatedTags { get; set; } = new() { "consolidated" };
}

/// <summary>
/// 记忆合并器。
/// 将多条记忆合并为摘要，使用 ADD-only 策略（不删除原始记忆）。
/// 无 LLM 参与，纯文本处理：按时间排序，提取关键内容（第一段 + 最后一段）。
/// </summary>
public class MemoryConsolidator
{
    private readonly IMemoryRepository _repository;
    private readonly ConsolidatorOptions _options;
    private readonly ILogger<MemoryConsolidator>? _logger;

    /// <summary>
    /// 创建 MemoryConsolidator 实例。
    /// </summary>
    /// <param name="repository">记忆存储</param>
    /// <param name="options">合并配置选项</param>
    /// <param name="logger">日志记录器</param>
    public MemoryConsolidator(
        IMemoryRepository repository,
        ConsolidatorOptions? options = null,
        ILogger<MemoryConsolidator>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? new ConsolidatorOptions();
        _logger = logger;
    }

    /// <summary>
    /// 合并多条记忆为摘要。
    /// ADD-only 策略：保留所有原始记忆，创建新的合并摘要。
    /// </summary>
    /// <param name="memories">待合并的记忆条目集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>合并结果</returns>
    public async Task<ConsolidationResult> ConsolidateAsync(
        IEnumerable<MemoryEntry> memories,
        CancellationToken cancellationToken = default)
    {
        if (memories == null)
        {
            return new ConsolidationResult
            {
                Success = false,
                Message = "记忆集合不能为空"
            };
        }

        var memoryList = memories.ToList();

        if (memoryList.Count == 0)
        {
            return new ConsolidationResult
            {
                Success = false,
                Message = "记忆集合为空"
            };
        }

        if (memoryList.Count == 1)
        {
            return new ConsolidationResult
            {
                Success = false,
                Message = "单条记忆无需合并"
            };
        }

        _logger?.LogInformation("开始合并 {Count} 条记忆", memoryList.Count);

        // 1. 按时间排序（使用 ValidAt 作为有效时间）
        var sortedMemories = memoryList
            .OrderBy(m => m.ValidAt)
            .ToList();

        var earliest = sortedMemories.First().ValidAt;
        var latest = sortedMemories.Last().ValidAt;

        _logger?.LogDebug("记忆时间范围: {Earliest} 至 {Latest}", earliest, latest);

        // 2. 提取关键内容（第一段 + 最后一段）
        var consolidatedContent = BuildConsolidatedContent(sortedMemories);

        // 3. 合并元数据
        var mergedMetadata = BuildMergedMetadata(sortedMemories);

        // 4. 创建合并记忆条目
        var consolidatedEntry = new MemoryEntry(
            Id: $"consolidated_{Guid.NewGuid():N}",
            Type: DetermineConsolidatedType(sortedMemories),
            Content: consolidatedContent,
            Metadata: mergedMetadata,
            CreatedAt: DateTimeOffset.UtcNow,
            ValidAt: earliest, // 使用最早的有效时间
            InvalidAt: latest  // 使用最晚的有效时间作为失效时间
        );

        // 5. 保存合并记忆（ADD-only：不删除原始记忆）
        await _repository.SaveMemoryAsync(consolidatedEntry);

        _logger?.LogInformation(
            "合并完成: 创建合并记忆 {ConsolidatedId}, 基于 {Count} 条原始记忆",
            consolidatedEntry.Id, memoryList.Count);

        return new ConsolidationResult
        {
            Success = true,
            ConsolidatedMemoryId = consolidatedEntry.Id,
            SourceCount = memoryList.Count,
            RetainedCount = memoryList.Count, // ADD-only 策略：保留所有原始记忆
            Message = $"ADD-only 策略：保留 {memoryList.Count} 条原始记忆，创建 1 条合并摘要",
            TimeRange = (earliest, latest)
        };
    }

    /// <summary>
    /// 构建合并内容。
    /// 提取关键内容：第一段 + 最后一段。
    /// </summary>
    private string BuildConsolidatedContent(List<MemoryEntry> sortedMemories)
    {
        var sb = new StringBuilder();
        var firstMemory = sortedMemories.First();
        var lastMemory = sortedMemories.Last();

        sb.AppendLine("**记忆合并摘要**");
        sb.AppendLine();

        // 提取第一段（最早记忆的内容）
        sb.AppendLine("### 最早记录");
        if (_options.IncludeTimestamps)
        {
            sb.AppendLine($"_时间: {firstMemory.ValidAt:yyyy-MM-dd HH:mm:ss}_");
        }

        var firstParagraph = ExtractFirstParagraph(firstMemory.Content);
        sb.AppendLine(firstParagraph);
        sb.AppendLine();

        // 中间记录摘要（如果有 3 条以上）
        if (sortedMemories.Count > 2)
        {
            sb.AppendLine("### 中间记录");
            sb.AppendLine($"_共 {sortedMemories.Count - 2} 条中间记忆被合并_");
            sb.AppendLine();
        }

        // 提取最后一段（最晚记忆的内容）
        sb.AppendLine("### 最新记录");
        if (_options.IncludeTimestamps)
        {
            sb.AppendLine($"_时间: {lastMemory.ValidAt:yyyy-MM-dd HH:mm:ss}_");
        }

        var lastParagraph = ExtractFirstParagraph(lastMemory.Content);
        sb.AppendLine(lastParagraph);
        sb.AppendLine();

        // 来源信息
        if (_options.IncludeSourceIds)
        {
            sb.AppendLine("---");
            sb.AppendLine($"**来源记忆**: {string.Join(", ", sortedMemories.Select(m => m.Id))}");
        }

        sb.AppendLine($"**合并时间**: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**合并策略**: ADD-only（原始记忆已保留）");

        var content = sb.ToString();

        // 截断到最大长度
        if (content.Length > _options.MaxSummaryLength)
        {
            content = content.Substring(0, _options.MaxSummaryLength - 3) + "...";
        }

        return content;
    }

    /// <summary>
    /// 提取内容的第一个段落。
    /// </summary>
    private string ExtractFirstParagraph(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "[空内容]";
        }

        // 按双换行分割段落
        var paragraphs = content.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        if (paragraphs.Length == 0)
        {
            return content.Trim();
        }

        // 返回第一个非空段落
        return paragraphs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim() ?? content.Trim();
    }

    /// <summary>
    /// 构建合并后的元数据。
    /// </summary>
    private MemoryMetadata BuildMergedMetadata(List<MemoryEntry> sortedMemories)
    {
        // 合并所有标签
        var allTags = new HashSet<string>();
        foreach (var tag in _options.ConsolidatedTags)
        {
            allTags.Add(tag);
        }

        foreach (var memory in sortedMemories)
        {
            if (memory.Metadata?.Tags != null)
            {
                foreach (var tag in memory.Metadata.Tags)
                {
                    allTags.Add(tag);
                }
            }
        }

        // 取最高置信度和重要性
        var maxConfidence = sortedMemories.Max(m => m.Metadata?.Confidence ?? 0.5);
        var maxImportance = sortedMemories.Max(m => m.Metadata?.Importance ?? 0.5);
        var totalAccessCount = sortedMemories.Sum(m => m.Metadata?.AccessCount ?? 0);

        // 使用第一条记忆的基本信息
        var firstMemory = sortedMemories.First();

        return new MemoryMetadata(
            SessionId: firstMemory.Metadata?.SessionId ?? string.Empty,
            AgentId: firstMemory.Metadata?.AgentId ?? string.Empty,
            Source: "consolidator_merge",
            Tags: allTags.ToList(),
            Confidence: maxConfidence,
            Importance: maxImportance,
            AccessCount: totalAccessCount
        );
    }

    /// <summary>
    /// 确定合并后的记忆类型。
    /// </summary>
    private MemoryType DetermineConsolidatedType(List<MemoryEntry> memories)
    {
        // 如果所有记忆类型相同，使用该类型
        var distinctTypes = memories.Select(m => m.Type).Distinct().ToList();
        if (distinctTypes.Count == 1)
        {
            return distinctTypes[0];
        }

        // 否则使用 Archive 类型表示归档合并
        return MemoryType.Archive;
    }
}