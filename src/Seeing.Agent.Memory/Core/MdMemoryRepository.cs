using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Memory.Core;

/// <summary>
/// Markdown 文件存储的 Memory Repository 实现。
/// 文件格式：YAML front matter + Markdown body
/// 文件路径：.seeing/memory/{type}/{sessionId}_{timestamp}_{uuid}.md
/// </summary>
public class MdMemoryRepository : IMemoryRepository
{
    private readonly string _baseDirectory;
    private readonly ILogger<MdMemoryRepository>? _logger;

    // 文件锁超时时间
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    // 文件锁字典，用于同一进程内的并发控制（V1 单进程）
    private static readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
    private static readonly object _lockDictLock = new();

    // YAML 序列化器
    private static readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// 创建 MdMemoryRepository 实例
    /// </summary>
    /// <param name="baseDirectory">基础目录路径，默认为 ~/.seeing/memory</param>
    /// <param name="logger">日志记录器</param>
    public MdMemoryRepository(string? baseDirectory = null, ILogger<MdMemoryRepository>? logger = null)
    {
        _logger = logger;
        _baseDirectory = baseDirectory ?? GetDefaultMemoryDirectory();
        EnsureDirectoriesExist();
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
    /// 确保所有类型目录存在
    /// </summary>
    private void EnsureDirectoriesExist()
    {
        var types = new[] { "semantic", "episodic", "procedural", "archive" };
        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (!Directory.Exists(typeDir))
            {
                Directory.CreateDirectory(typeDir);
                _logger?.LogInformation("创建记忆存储目录: {Directory}", typeDir);
            }
        }
    }

    /// <summary>
    /// 获取记忆文件路径
    /// </summary>
    private string GetMemoryFilePath(MemoryEntry entry)
    {
        ValidateMemoryId(entry.Id);
        var typeDir = GetTypeDirectory(entry.Type);
        return Path.Combine(typeDir, $"{entry.Metadata.SessionId}_{entry.ValidAt.ToUnixTimeMilliseconds()}_{entry.Id}.md");
    }

    /// <summary>
    /// 根据文件名获取文件路径
    /// </summary>
    private string GetMemoryFilePathById(string memoryId)
    {
        ValidateMemoryId(memoryId);

        // 搜索所有类型目录查找文件
        var types = new[] { "semantic", "episodic", "procedural", "archive" };
        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (Directory.Exists(typeDir))
            {
                var files = Directory.GetFiles(typeDir, $"*_{memoryId}.md");
                if (files.Length > 0)
                {
                    return files[0];
                }
            }
        }

        // 返回假设路径（用于错误报告）
        return Path.Combine(_baseDirectory, "unknown", $"{memoryId}.md");
    }

    /// <summary>
    /// 获取类型目录路径
    /// </summary>
    private string GetTypeDirectory(MemoryType type)
    {
        return Path.Combine(_baseDirectory, type.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// 验证记忆ID，防止路径遍历攻击
    /// </summary>
    private static void ValidateMemoryId(string memoryId)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("记忆ID不能为空", nameof(memoryId));
        }

        // 检查路径遍历攻击（防止访问上级目录）
        if (memoryId.Contains("..") || memoryId.Contains("/") || memoryId.Contains("\\"))
        {
            throw new ArgumentException("记忆ID包含非法路径字符", nameof(memoryId));
        }

        // 检查非法文件名字符
        var invalidChars = Path.GetInvalidFileNameChars();
        if (memoryId.IndexOfAny(invalidChars) >= 0)
        {
            throw new ArgumentException("记忆ID包含非法字符", nameof(memoryId));
        }
    }

    /// <summary>
    /// 验证 sessionId，防止路径遍历攻击
    /// </summary>
    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("SessionId不能为空", nameof(sessionId));
        }

        if (sessionId.Contains("..") || sessionId.Contains("/") || sessionId.Contains("\\"))
        {
            throw new ArgumentException("SessionId包含非法路径字符", nameof(sessionId));
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        if (sessionId.IndexOfAny(invalidChars) >= 0)
        {
            throw new ArgumentException("SessionId包含非法字符", nameof(sessionId));
        }
    }

    /// <summary>
    /// 获取文件锁
    /// </summary>
    private SemaphoreSlim GetFileLock(string filePath)
    {
        lock (_lockDictLock)
        {
            if (!_fileLocks.TryGetValue(filePath, out var fileLock))
            {
                fileLock = new SemaphoreSlim(1, 1);
                _fileLocks[filePath] = fileLock;
            }
            return fileLock;
        }
    }

    /// <summary>
    /// 保存记忆到 Markdown 文件
    /// </summary>
    public async Task SaveMemoryAsync(object memory)
    {
        if (memory == null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        var entry = memory as MemoryEntry
            ?? throw new ArgumentException("memory 必须是 MemoryEntry 类型", nameof(memory));

        ValidateMemoryId(entry.Id);
        ValidateSessionId(entry.Metadata.SessionId);

        var filePath = GetMemoryFilePath(entry);
        var fileLock = GetFileLock(filePath);

        if (!await fileLock.WaitAsync(LockTimeout))
        {
            _logger?.LogWarning("获取文件锁超时: {MemoryId}", entry.Id);
            throw new TimeoutException("获取文件锁超时，请稍后重试");
        }

        try
        {
            var content = BuildMarkdownContent(entry);

            // 使用临时文件 + 原子替换确保写入完整性
            var tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8);

            // 原子替换
            File.Move(tempPath, filePath, overwrite: true);

            _logger?.LogDebug("保存记忆成功: {MemoryId} -> {FilePath}", entry.Id, filePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 构建 Markdown 内容（YAML front matter + body）
    /// </summary>
    private string BuildMarkdownContent(MemoryEntry entry)
    {
        var frontMatter = new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["type"] = entry.Type.ToString().ToLowerInvariant(),
            ["sessionId"] = entry.Metadata.SessionId,
            ["createdAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["validAt"] = entry.ValidAt.ToUnixTimeMilliseconds(),
            ["invalidAt"] = entry.InvalidAt?.ToUnixTimeMilliseconds(),
            ["agentId"] = entry.Metadata.AgentId,
            ["source"] = entry.Metadata.Source,
            ["tags"] = entry.Metadata.Tags,
            ["confidence"] = entry.Metadata.Confidence,
            ["importance"] = entry.Metadata.Importance
        };

        var yaml = _yamlSerializer.Serialize(frontMatter);

        return $"---\n{yaml}---\n\n{entry.Content}";
    }

    /// <summary>
    /// 从文件加载记忆
    /// </summary>
    public async Task<object> GetMemoryAsync(string memoryId)
    {
        ValidateMemoryId(memoryId);

        var filePath = GetMemoryFilePathById(memoryId);

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("记忆文件不存在: {FilePath}", filePath);
            return null!;
        }

        var fileLock = GetFileLock(filePath);

        if (!await fileLock.WaitAsync(LockTimeout))
        {
            _logger?.LogWarning("获取文件锁超时: {MemoryId}", memoryId);
            throw new TimeoutException("获取文件锁超时，请稍后重试");
        }

        try
        {
            var result = await ReadMemoryFileAsync(filePath, memoryId);
            return result ?? null!;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 读取记忆文件（内部方法）
    /// </summary>
    private async Task<MemoryEntry?> ReadMemoryFileAsync(string filePath, string memoryId)
    {
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
            var content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger?.LogWarning("记忆文件为空: {MemoryId}", memoryId);
                return null;
            }

            return ParseMarkdownContent(content);
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "读取记忆文件失败: {MemoryId}", memoryId);
            throw new MemoryLoadException(memoryId, filePath, "读取记忆文件失败", ex);
        }
    }

    /// <summary>
    /// 解析 Markdown 内容为 MemoryEntry
    /// </summary>
    private MemoryEntry? ParseMarkdownContent(string content)
    {
        var frontMatter = YamlParser.ParseYamlFrontMatter(content);
        var body = YamlParser.ExtractMarkdownBody(content);

        if (frontMatter.Count == 0)
        {
            _logger?.LogWarning("无法解析 YAML front matter");
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
            _logger?.LogWarning("记忆文件缺少必要字段: id, type, sessionId, validAt");
            return null;
        }

        // 解析类型
        if (!Enum.TryParse<MemoryType>(typeStr, ignoreCase: true, out var type))
        {
            _logger?.LogWarning("无法解析记忆类型: {Type}", typeStr);
            type = MemoryType.Semantic; // 默认类型
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
            : DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value); // 默认使用 validAt
        var validAt = DateTimeOffset.FromUnixTimeMilliseconds(validAtMs.Value);
        var invalidAt = invalidAtMs.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidAtMs.Value)
            : (DateTimeOffset?)null;

        return new MemoryEntry(id, type, body, metadata, createdAt, validAt, invalidAt);
    }

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

    /// <summary>
    /// 列出所有记忆
    /// </summary>
    public async Task<IEnumerable<object>> ListMemoriesAsync()
    {
        return await Task.FromResult(EnumerateMemories());
    }

    /// <summary>
    /// 枚举所有记忆
    /// </summary>
    private IEnumerable<MemoryEntry> EnumerateMemories()
    {
        var types = new[] { "semantic", "episodic", "procedural", "archive" };

        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (!Directory.Exists(typeDir))
                continue;

            foreach (var filePath in Directory.GetFiles(typeDir, "*.md"))
            {
                MemoryEntry? entry = null;

                try
                {
                    entry = ReadMemoryFileSync(filePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "跳过损坏的记忆文件: {FilePath}", filePath);
                    continue;
                }

                if (entry != null)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// 同步读取记忆文件（用于枚举）
    /// </summary>
    private MemoryEntry? ReadMemoryFileSync(string filePath)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);
        return ParseMarkdownContent(content);
    }

    /// <summary>
    /// 删除记忆文件
    /// </summary>
    public async Task DeleteMemoryAsync(string memoryId)
    {
        ValidateMemoryId(memoryId);

        var filePath = GetMemoryFilePathById(memoryId);

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("记忆文件不存在，无需删除: {FilePath}", filePath);
            return;
        }

        var fileLock = GetFileLock(filePath);

        if (!await fileLock.WaitAsync(LockTimeout))
        {
            _logger?.LogWarning("获取文件锁超时: {MemoryId}", memoryId);
            throw new TimeoutException("获取文件锁超时，请稍后重试");
        }

        try
        {
            File.Delete(filePath);
            _logger?.LogDebug("删除记忆成功: {MemoryId}", memoryId);
        }
        finally
        {
            fileLock.Release();
        }
    }
}

/// <summary>
/// 记忆加载异常
/// </summary>
public class MemoryLoadException : Exception
{
    public string MemoryId { get; }
    public string FilePath { get; }

    public MemoryLoadException(string memoryId, string filePath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        MemoryId = memoryId;
        FilePath = filePath;
    }
}