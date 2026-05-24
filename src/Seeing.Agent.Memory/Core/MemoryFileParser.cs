using Microsoft.Extensions.Logging;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 记忆文件格式解析器，支持解析和序列化 Markdown 格式的记忆文件。
/// 文件格式：YAML front matter + Markdown body
/// </summary>
public static class MemoryFileParser
{
    // YAML 序列化器（线程安全）
    private static readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// 从文件路径解析记忆文件，返回 MemoryEntry 对象。
    /// </summary>
    /// <param name="filePath">记忆文件路径</param>
    /// <param name="logger">可选的日志记录器</param>
    /// <returns>解析后的 MemoryEntry，解析失败返回 null</returns>
    /// <exception cref="ArgumentNullException">filePath 为空</exception>
    /// <exception cref="FileNotFoundException">文件不存在</exception>
    /// <exception cref="MemoryLoadException">文件读取或解析失败</exception>
    public static MemoryEntry? ParseMemoryFile(string filePath, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"记忆文件不存在: {filePath}", filePath);
        }

        try
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseMemoryContent(content, logger);
        }
        catch (IOException ex)
        {
            logger?.LogError(ex, "读取记忆文件失败: {FilePath}", filePath);
            throw new MemoryLoadException(Path.GetFileNameWithoutExtension(filePath), filePath, "读取记忆文件失败", ex);
        }
    }

    /// <summary>
    /// 从文件路径异步解析记忆文件，返回 MemoryEntry 对象。
    /// </summary>
    /// <param name="filePath">记忆文件路径</param>
    /// <param name="logger">可选的日志记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>解析后的 MemoryEntry，解析失败返回 null</returns>
    public static async Task<MemoryEntry?> ParseMemoryFileAsync(
        string filePath,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"记忆文件不存在: {filePath}", filePath);
        }

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken);

            return ParseMemoryContent(content, logger);
        }
        catch (IOException ex)
        {
            logger?.LogError(ex, "读取记忆文件失败: {FilePath}", filePath);
            throw new MemoryLoadException(Path.GetFileNameWithoutExtension(filePath), filePath, "读取记忆文件失败", ex);
        }
    }

    /// <summary>
    /// 解析 Markdown 内容为 MemoryEntry。
    /// </summary>
    /// <param name="content">Markdown 内容（YAML front matter + body）</param>
    /// <param name="logger">可选的日志记录器</param>
    /// <returns>解析后的 MemoryEntry，解析失败返回 null</returns>
    public static MemoryEntry? ParseMemoryContent(string content, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            logger?.LogWarning("记忆内容为空");
            return null;
        }

        var frontMatter = YamlParser.ParseYamlFrontMatter(content);
        var body = YamlParser.ExtractMarkdownBody(content);

        if (frontMatter.Count == 0)
        {
            logger?.LogWarning("无法解析 YAML front matter");
            return null;
        }

        // 提取必要字段
        var id = GetStringValue(frontMatter, "id");
        var typeStr = GetStringValue(frontMatter, "type");
        var sessionId = GetStringValue(frontMatter, "sessionId");
        var validAtMs = GetLongValue(frontMatter, "validAt");

        // 验证必要字段
        if (id == null || typeStr == null || sessionId == null || validAtMs == null)
        {
            logger?.LogWarning("记忆文件缺少必要字段: id, type, sessionId, validAt");
            return null;
        }

        // 解析类型
        if (!Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var type))
        {
            logger?.LogWarning("无法解析记忆类型: {Type}，使用默认类型 Semantic", typeStr);
            type = MemoryType.Semantic;
        }

        // 提取可选字段
        var agentId = GetStringValue(frontMatter, "agentId") ?? string.Empty;
        var source = GetStringValue(frontMatter, "source") ?? string.Empty;
        var createdAtMs = GetLongValue(frontMatter, "createdAt");
        var invalidAtMs = GetLongValue(frontMatter, "invalidAt");
        var confidence = GetDoubleValue(frontMatter, "confidence") ?? 0.5;
        var importance = GetDoubleValue(frontMatter, "importance") ?? 0.5;
        var tags = GetListValue(frontMatter, "tags") ?? new List<string>();

        // 构建 Metadata
        var metadata = new MemoryMetadata(
            sessionId,
            agentId,
            source,
            tags,
            confidence,
            importance
        );

        // 构建 DateTimeOffset
        var createdAt = createdAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs.Value)
            : DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value); // 默认使用 validAt
        var validAt = DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value);
        var invalidAt = invalidAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidAtMs.Value)
            : (DateTimeOffset?)null;

        return new MemoryEntry(id, type, body, metadata, createdAt, validAt, invalidAt);
    }

    /// <summary>
    /// 将 MemoryEntry 序列化为 Markdown 格式字符串。
    /// </summary>
    /// <param name="memory">要序列化的记忆条目</param>
    /// <returns>Markdown 格式字符串（YAML front matter + body）</returns>
    /// <exception cref="ArgumentNullException">memory 为 null</exception>
    public static string SerializeToMarkdown(MemoryEntry memory)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        var frontMatter = new Dictionary<string, object?>
        {
            ["id"] = memory.Id,
            ["type"] = memory.Type.ToString().ToLowerInvariant(),
            ["sessionId"] = memory.Metadata.SessionId,
            ["createdAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["validAt"] = memory.ValidAt.ToUnixTimeMilliseconds(),
            ["invalidAt"] = memory.InvalidAt?.ToUnixTimeMilliseconds(),
            ["agentId"] = memory.Metadata.AgentId,
            ["source"] = memory.Metadata.Source,
            ["tags"] = memory.Metadata.Tags,
            ["confidence"] = memory.Metadata.Confidence,
            ["importance"] = memory.Metadata.Importance
        };

        var yaml = _yamlSerializer.Serialize(frontMatter);

        return $"---\n{yaml}---\n\n{memory.Content}";
    }

    /// <summary>
    /// 将 MemoryEntry 异步保存到文件。
    /// </summary>
    /// <param name="memory">要保存的记忆条目</param>
    /// <param name="filePath">目标文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task SaveToFileAsync(
        MemoryEntry memory,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        var content = SerializeToMarkdown(memory);

        // 确保目录存在
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 使用临时文件 + 原子替换确保写入完整性
        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, cancellationToken);

        File.Move(tempPath, filePath, overwrite: true);
    }

    #region 私有辅助方法

    /// <summary>
    /// 从 front matter 获取字符串值
    /// </summary>
    private static string? GetStringValue(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;
        return value.ToString();
    }

    /// <summary>
    /// 从 front matter 获取长整型值
    /// </summary>
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

    /// <summary>
    /// 从 front matter 获取双精度值
    /// </summary>
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

    /// <summary>
    /// 从 front matter 获取列表值
    /// </summary>
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

        // 单个字符串转换为列表
        if (value is string s)
        {
            return new List<string> { s };
        }

        return null;
    }

    #endregion
}