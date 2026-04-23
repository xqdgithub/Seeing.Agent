using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// MemoryChunkManager 协调 MemoryChunker 和 MdMemoryRepository，
/// 提供大内容的自动分块保存和加载合并功能。
/// 分块文件命名约定：{sessionId}_{timestamp}_{baseId}_chunk{n}.md
/// </summary>
public class MemoryChunkManager
{
    private readonly IMemoryRepository _repository;
    private readonly string _baseDirectory;
    private readonly ILogger<MemoryChunkManager>? _logger;

    // 分块文件名模式：匹配 _chunk{数字}.md
    private static readonly Regex ChunkSuffixPattern = new(@"_chunk(\d+)\.md$", RegexOptions.Compiled);

    /// <summary>
    /// 创建 MemoryChunkManager 实例
    /// </summary>
    /// <param name="repository">记忆存储仓库</param>
    /// <param name="baseDirectory">基础目录路径，默认为 ~/.seeing/memory</param>
    /// <param name="logger">日志记录器</param>
    public MemoryChunkManager(
        IMemoryRepository repository,
        string? baseDirectory = null,
        ILogger<MemoryChunkManager>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
        _baseDirectory = baseDirectory ?? GetDefaultMemoryDirectory();
    }

    /// <summary>
    /// 获取默认记忆目录路径 (~/.seeing/memory)
    /// </summary>
    private static string GetDefaultMemoryDirectory()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDir, ".seeing", "memory");
    }

    /// <summary>
    /// 加载所有分块并合并为完整的 MemoryEntry
    /// </summary>
    /// <param name="baseId">基础记忆ID（不带分块后缀）</param>
    /// <returns>合并后的 MemoryEntry，如果不存在则返回 null</returns>
    public async Task<MemoryEntry?> LoadAllChunksAsync(string baseId)
    {
        ValidateMemoryId(baseId);

        // 首先尝试加载非分块的原始文件
        var baseEntry = await _repository.GetMemoryAsync(baseId) as MemoryEntry;

        // 查找所有分块文件
        var chunkFiles = FindChunkFiles(baseId);

        if (chunkFiles.Count == 0)
        {
            // 没有分块文件，返回原始文件（可能为 null）
            return baseEntry;
        }

        // 有分块文件，需要合并
        _logger?.LogDebug("发现 {Count} 个分块文件需要合并: {BaseId}", chunkFiles.Count, baseId);

        // 按分块序号排序
        var sortedChunks = chunkFiles
            .OrderBy(f => f.Item1) // 按分块序号排序
            .ToList();

        // 读取所有分块内容
        var chunkContents = new List<string>();
        foreach (var (_, filePath) in sortedChunks)
        {
            var entry = await ReadMemoryFileAsync(filePath);
            if (entry != null)
            {
                chunkContents.Add(entry.Content);
            }
        }

        if (chunkContents.Count == 0)
        {
            _logger?.LogWarning("无法读取任何分块内容: {BaseId}", baseId);
            return baseEntry;
        }

        // 合并分块内容
        var mergedContent = MemoryChunker.MergeChunks(chunkContents);

        // 如果有原始条目，使用其元数据；否则创建新条目
        if (baseEntry != null)
        {
            return baseEntry with { Content = mergedContent };
        }

        // 没有原始条目，从第一个分块获取元数据
        var firstChunk = await ReadMemoryFileAsync(sortedChunks[0].Item2);
        if (firstChunk != null)
        {
            return firstChunk with
            {
                Id = baseId,
                Content = mergedContent
            };
        }

        _logger?.LogWarning("无法合并分块: 无法读取元数据");
        return null;
    }

    /// <summary>
    /// 保存 MemoryEntry，自动分块处理大内容
    /// </summary>
    /// <param name="memory">要保存的记忆条目</param>
    /// <returns>保存的分块数量（1表示未分块，>1表示分块数量）</returns>
    public async Task<int> SaveAllChunksAsync(MemoryEntry memory)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        ValidateMemoryId(memory.Id);

        // 先删除旧的分块文件（如果存在）
        await DeleteChunkFilesAsync(memory.Id);

        // 检查是否需要分块
        if (!MemoryChunker.ShouldChunk(memory.Content))
        {
            // 不需要分块，直接保存
            await _repository.SaveMemoryAsync(memory);
            _logger?.LogDebug("保存记忆（无分块）: {MemoryId}", memory.Id);
            return 1;
        }

        // 需要分块
        var chunks = MemoryChunker.ChunkContent(memory.Content);
        _logger?.LogInformation("内容超过 50KB，自动分块为 {Count} 个分块: {MemoryId}", chunks.Count, memory.Id);

        // 保存每个分块
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkId = GetChunkId(memory.Id, i + 1);
            var chunkEntry = memory with
            {
                Id = chunkId,
                Content = chunks[i]
            };

            await _repository.SaveMemoryAsync(chunkEntry);
        }

        // 创建或更新基础索引文件（保存元数据，内容为空或摘要）
        var baseEntry = memory with
        {
            Content = $"<!-- 此记忆已分块存储，共 {chunks.Count} 个分块 -->"
        };
        await _repository.SaveMemoryAsync(baseEntry);

        _logger?.LogDebug("保存记忆（分块）: {MemoryId}, 分块数: {Count}", memory.Id, chunks.Count);
        return chunks.Count;
    }

    /// <summary>
    /// 获取指定记忆的分块数量
    /// </summary>
    /// <param name="baseId">基础记忆ID</param>
    /// <returns>分块数量（0表示不存在，1表示未分块，>1表示分块数量）</returns>
    public int GetChunkCount(string baseId)
    {
        ValidateMemoryId(baseId);

        // 检查基础文件是否存在
        var baseFilePath = FindMemoryFile(baseId);
        if (baseFilePath == null)
        {
            return 0;
        }

        // 查找分块文件
        var chunkFiles = FindChunkFiles(baseId);

        if (chunkFiles.Count == 0)
        {
            // 没有分块文件，返回 1 表示未分块的单文件
            return 1;
        }

        // 返回分块数量
        return chunkFiles.Count;
    }

    /// <summary>
    /// 删除记忆及其所有分块
    /// </summary>
    /// <param name="baseId">基础记忆ID</param>
    public async Task DeleteAllChunksAsync(string baseId)
    {
        ValidateMemoryId(baseId);

        // 删除基础文件
        await _repository.DeleteMemoryAsync(baseId);

        // 删除所有分块文件
        await DeleteChunkFilesAsync(baseId);

        _logger?.LogDebug("删除记忆及其分块: {BaseId}", baseId);
    }

    /// <summary>
    /// 获取分块ID
    /// </summary>
    private static string GetChunkId(string baseId, int chunkIndex)
    {
        return $"{baseId}_chunk{chunkIndex}";
    }

    /// <summary>
    /// 查找所有分块文件
    /// </summary>
    /// <returns>分块序号和文件路径的元组列表</returns>
    private List<(int, string)> FindChunkFiles(string baseId)
    {
        var result = new List<(int, string)>();
        var types = new[] { "semantic", "episodic", "procedural", "archive" };

        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (!Directory.Exists(typeDir))
                continue;

            // 查找匹配的分块文件：*_chunk{baseId}_chunk{n}.md 或 *_{baseId}_chunk{n}.md
            // 文件名格式：{sessionId}_{timestamp}_{baseId}_chunk{n}.md
            var pattern = $"*_{baseId}_chunk*.md";
            var files = Directory.GetFiles(typeDir, pattern);

            foreach (var file in files)
            {
                var match = ChunkSuffixPattern.Match(file);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var chunkNum))
                {
                    result.Add((chunkNum, file));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 查找记忆文件
    /// </summary>
    private string? FindMemoryFile(string memoryId)
    {
        var types = new[] { "semantic", "episodic", "procedural", "archive" };

        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (!Directory.Exists(typeDir))
                continue;

            var files = Directory.GetFiles(typeDir, $"*_{memoryId}.md");
            if (files.Length > 0)
            {
                return files[0];
            }
        }

        return null;
    }

    /// <summary>
    /// 读取记忆文件
    /// </summary>
    private async Task<MemoryEntry?> ReadMemoryFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            return ParseMemoryContent(content);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取记忆文件失败: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 解析记忆文件内容
    /// </summary>
    private MemoryEntry? ParseMemoryContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var frontMatter = YamlParser.ParseYamlFrontMatter(content);
        var body = YamlParser.ExtractMarkdownBody(content);

        if (frontMatter.Count == 0)
        {
            return null;
        }

        // 提取必要字段
        var id = GetStringValue(frontMatter, "id");
        var typeStr = GetStringValue(frontMatter, "type");
        var sessionId = GetStringValue(frontMatter, "sessionId");
        var agentId = GetStringValue(frontMatter, "agentId");
        var source = GetStringValue(frontMatter, "source");
        var createdAtMs = GetLongValue(frontMatter, "createdAt");
        var validAtMs = GetLongValue(frontMatter, "validAt");
        var invalidAtMs = GetLongValue(frontMatter, "invalidAt");
        var confidence = GetDoubleValue(frontMatter, "confidence");
        var importance = GetDoubleValue(frontMatter, "importance");
        var tags = GetListValue(frontMatter, "tags");

        if (id == null || typeStr == null || sessionId == null || validAtMs == null)
        {
            return null;
        }

        // 解析类型
        if (!Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var type))
        {
            type = MemoryType.Semantic;
        }

        // 构建 Metadata
        var metadata = new MemoryMetadata(
            sessionId,
            agentId ?? string.Empty,
            source ?? string.Empty,
            tags ?? new List<string>(),
            confidence ?? 0.5,
            importance ?? 0.5
        );

        // 构建 DateTimeOffset
        var createdAt = createdAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs.Value)
            : DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value);
        var validAt = DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value);
        var invalidAt = invalidAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidAtMs.Value)
            : (DateTimeOffset?)null;

        return new MemoryEntry(id, type, body, metadata, createdAt, validAt, invalidAt);
    }

    /// <summary>
    /// 删除所有分块文件
    /// </summary>
    private async Task DeleteChunkFilesAsync(string baseId)
    {
        var chunkFiles = FindChunkFiles(baseId);

        foreach (var (_, filePath) in chunkFiles)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    // 从文件名提取分块ID
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    var chunkIdMatch = Regex.Match(fileName, @"_(chunk\d+)$");
                    if (chunkIdMatch.Success)
                    {
                        var chunkId = baseId + "_" + chunkIdMatch.Groups[1].Value;
                        await _repository.DeleteMemoryAsync(chunkId);
                    }
                    else
                    {
                        // 直接删除文件
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "删除分块文件失败: {FilePath}", filePath);
            }
        }
    }

    /// <summary>
    /// 验证记忆ID
    /// </summary>
    private static void ValidateMemoryId(string memoryId)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("记忆ID不能为空", nameof(memoryId));
        }

        if (memoryId.Contains("..") || memoryId.Contains("/") || memoryId.Contains("\\"))
        {
            throw new ArgumentException("记忆ID包含非法路径字符", nameof(memoryId));
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        if (memoryId.IndexOfAny(invalidChars) >= 0)
        {
            throw new ArgumentException("记忆ID包含非法字符", nameof(memoryId));
        }
    }

    #region Helper methods for parsing YAML front matter

    private static string? GetStringValue(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;
        return value.ToString();
    }

    private static long? GetLongValue(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is long l)
            return l;

        if (value is int i)
            return i;

        if (double.TryParse(value.ToString(), out var d))
            return (long)d;

        return null;
    }

    private static double? GetDoubleValue(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is double d)
            return d;

        if (value is float f)
            return f;

        if (double.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static IReadOnlyList<string>? GetListValue(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is List<object?> list)
        {
            return list.Select(v => v?.ToString() ?? string.Empty).ToList();
        }

        if (value is string[] arr)
        {
            return arr;
        }

        if (value is string s)
        {
            return new List<string> { s };
        }

        return null;
    }

    #endregion
}