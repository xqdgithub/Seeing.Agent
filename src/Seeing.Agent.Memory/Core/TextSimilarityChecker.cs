using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 基于 V1 文本相似度的检查器实现。
/// 使用关键词匹配和 Jaccard 相似度计算文本相似度。
/// </summary>
public class TextSimilarityChecker : ISimilarityChecker
{
    private readonly ILogger<TextSimilarityChecker>? _logger;
    private readonly TextSimilarityOptions _options;

    /// <summary>
    /// 创建 TextSimilarityChecker 实例。
    /// </summary>
    /// <param name="options">文本相似度配置选项</param>
    /// <param name="logger">日志记录器</param>
    public TextSimilarityChecker(TextSimilarityOptions? options = null, ILogger<TextSimilarityChecker>? logger = null)
    {
        _options = options ?? new TextSimilarityOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<double> CalculateSimilarityAsync(MemoryEntry entry1, MemoryEntry entry2)
    {
        if (entry1 == null || entry2 == null)
        {
            return Task.FromResult(0.0);
        }

        // 相同 ID 视为完全相同
        if (entry1.Id == entry2.Id)
        {
            return Task.FromResult(1.0);
        }

        var content1 = entry1.Content ?? string.Empty;
        var content2 = entry2.Content ?? string.Empty;

        // 完全匹配
        if (string.Equals(content1, content2, StringComparison.Ordinal))
        {
            _logger?.LogDebug("记忆 {Id1} 和 {Id2} 内容完全匹配", entry1.Id, entry2.Id);
            return Task.FromResult(1.0);
        }

        // 计算多个相似度指标
        var scores = new List<double>();

        // 1. 内容相似度（权重最高）
        var contentSimilarity = CalculateJaccardSimilarity(content1, content2);
        scores.Add(contentSimilarity * _options.ContentWeight);

        // 2. 标签相似度
        var tagSimilarity = CalculateTagSimilarity(entry1.Metadata?.Tags, entry2.Metadata?.Tags);
        scores.Add(tagSimilarity * _options.TagWeight);

        // 3. 类型相同加分
        if (entry1.Type == entry2.Type)
        {
            scores.Add(_options.TypeMatchBonus);
        }

        var totalSimilarity = Math.Min(1.0, scores.Sum());
        
        _logger?.LogDebug(
            "记忆相似度计算: {Id1} vs {Id2} -> 内容={Content:F2}, 标签={Tag:F2}, 总分={Total:F2}",
            entry1.Id, entry2.Id, contentSimilarity, tagSimilarity, totalSimilarity);

        return Task.FromResult(totalSimilarity);
    }

    /// <inheritdoc />
    public async Task<bool> IsDuplicateAsync(MemoryEntry entry1, MemoryEntry entry2, double threshold = 0.8)
    {
        var similarity = await CalculateSimilarityAsync(entry1, entry2);
        return similarity >= threshold;
    }

    /// <summary>
    /// 计算 Jaccard 相似度（集合交集 / 集合并集）。
    /// </summary>
    private double CalculateJaccardSimilarity(string text1, string text2)
    {
        if (string.IsNullOrWhiteSpace(text1) || string.IsNullOrWhiteSpace(text2))
        {
            return 0.0;
        }

        var words1 = ExtractKeywords(text1);
        var words2 = ExtractKeywords(text2);

        if (words1.Count == 0 || words2.Count == 0)
        {
            return 0.0;
        }

        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();

        if (union == 0)
        {
            return 0.0;
        }

        return (double)intersection / union;
    }

    /// <summary>
    /// 计算标签相似度。
    /// </summary>
    private double CalculateTagSimilarity(IReadOnlyList<string>? tags1, IReadOnlyList<string>? tags2)
    {
        if (tags1 == null || tags1.Count == 0 || tags2 == null || tags2.Count == 0)
        {
            return 0.0;
        }

        var set1 = new HashSet<string>(tags1, StringComparer.OrdinalIgnoreCase);
        var set2 = new HashSet<string>(tags2, StringComparer.OrdinalIgnoreCase);

        var intersection = set1.Intersect(set2, StringComparer.OrdinalIgnoreCase).Count();
        var union = set1.Union(set2, StringComparer.OrdinalIgnoreCase).Count();

        if (union == 0)
        {
            return 0.0;
        }

        return (double)intersection / union;
    }

    /// <summary>
    /// 从文本中提取关键词。
    /// </summary>
    private HashSet<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>();
        }

        // 简单分词：按空白和标点分割
        var words = text.Split(
            new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '_', '(', ')', '[', ']', '{', '}', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            // 过滤停用词和短词
            if (word.Length < _options.MinKeywordLength)
            {
                continue;
            }

            // 过滤停用词
            if (_options.StopWords.Contains(word.ToLowerInvariant()))
            {
                continue;
            }

            keywords.Add(word.ToLowerInvariant());
        }

        return keywords;
    }
}

/// <summary>
/// 文本相似度配置选项。
/// </summary>
public class TextSimilarityOptions
{
    /// <summary>
    /// 内容相似度权重。默认 0.7。
    /// </summary>
    public double ContentWeight { get; set; } = 0.7;

    /// <summary>
    /// 标签相似度权重。默认 0.2。
    /// </summary>
    public double TagWeight { get; set; } = 0.2;

    /// <summary>
    /// 类型匹配加分。默认 0.1。
    /// </summary>
    public double TypeMatchBonus { get; set; } = 0.1;

    /// <summary>
    /// 最小关键词长度。默认 2。
    /// </summary>
    public int MinKeywordLength { get; set; } = 2;

    /// <summary>
    /// 停用词列表。
    /// </summary>
    public HashSet<string> StopWords { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 英文停用词
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "must", "shall", "can", "this",
        "that", "these", "those", "it", "its", "they", "them", "their",
        "we", "us", "our", "you", "your", "he", "him", "his", "she", "her",
        
        // 中文停用词
        "的", "是", "在", "了", "和", "与", "或", "有", "不", "这", "那",
        "我", "你", "他", "她", "它", "们", "个", "也", "就", "都", "而",
        "及", "着", "过", "把", "被", "给", "让", "向", "对", "为", "以"
    };
}