using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;
using System.Reactive.Subjects;
using System.Text;

// Alias to resolve conflict with Core.MemoryType
using ReMeMemoryType = Seeing.Agent.Memory.Core.Models.MemoryType;

namespace Seeing.Agent.Memory.Core.Storage;

/// <summary>
/// 本地文件存储 - 实现 ReMe 格式的 Markdown 文件存储
/// 
/// 目录结构:
/// - session/     原始会话记录
/// - daily/       每日浅加工记忆
/// - digest/      长期记忆 (LLM 整合)
/// 
/// 文件格式: YAML frontmatter + Markdown body
/// </summary>
public class LocalFileStore : IFileStore, IDisposable
{
    private readonly string _baseDirectory;
    private readonly ILogger<LocalFileStore>? _logger;
    private readonly Subject<FileChangeEventArgs> _changes = new();
    private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly object _lockDictLock = new();

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>文件变更事件流</summary>
    public IObservable<FileChangeEventArgs> Changes => _changes;

    /// <summary>
    /// 创建 LocalFileStore 实例
    /// </summary>
    /// <param name="baseDirectory">基础目录路径</param>
    /// <param name="logger">日志记录器</param>
    public LocalFileStore(string? baseDirectory = null, ILogger<LocalFileStore>? logger = null)
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
        var types = new[] { "session", "daily", "digest" };
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
    /// 验证路径，防止路径遍历攻击
    /// </summary>
    private void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空", nameof(path));
        }

        // 标准化路径
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');

        // 检查路径遍历攻击
        if (normalizedPath.Contains(".."))
        {
            throw new ArgumentException("路径包含非法的遍历字符", nameof(path));
        }

        // 检查绝对路径
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("只接受相对路径", nameof(path));
        }
    }

    /// <summary>
    /// 获取完整文件路径
    /// </summary>
    private string GetFullPath(string relativePath)
    {
        return Path.Combine(_baseDirectory, relativePath);
    }

    /// <summary>
    /// 写入文件
    /// </summary>
    public async Task<FileNode> WriteAsync(string path, string content, CancellationToken ct = default)
    {
        ValidatePath(path);

        var fullPath = GetFullPath(path);
        var fileLock = GetFileLock(fullPath);

        if (!await fileLock.WaitAsync(LockTimeout, ct))
        {
            throw new TimeoutException($"获取文件锁超时: {path}");
        }

        try
        {
            // 确保目录存在
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 检查是否已存在
            var exists = File.Exists(fullPath);
            var changeType = exists ? FileChangeType.Modified : FileChangeType.Created;

            // 使用临时文件 + 原子替换确保写入完整性
            var tempPath = fullPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ct);

            // 原子替换
            File.Move(tempPath, fullPath, overwrite: true);

            _logger?.LogDebug("写入文件成功: {Path}", path);

            // 创建 FileNode (解析 YAML frontmatter)
            var node = CreateFileNode(path, content, fullPath);

            // 发送变更通知
            _changes.OnNext(new FileChangeEventArgs(path, changeType));

            return node;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 读取文件
    /// </summary>
    public async Task<FileNode?> ReadAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);

        var fullPath = GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            _logger?.LogDebug("文件不存在: {Path}", path);
            return null;
        }

        var fileLock = GetFileLock(fullPath);

        if (!await fileLock.WaitAsync(LockTimeout, ct))
        {
            throw new TimeoutException($"获取文件锁超时: {path}");
        }

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(ct);

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // 解析 YAML frontmatter 并创建 FileNode
            var node = CreateFileNode(path, content, fullPath);

            return node;
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "读取文件失败: {Path}", path);
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);

        var fullPath = GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            _logger?.LogDebug("文件不存在，无需删除: {Path}", path);
            return;
        }

        var fileLock = GetFileLock(fullPath);

        if (!await fileLock.WaitAsync(LockTimeout, ct))
        {
            throw new TimeoutException($"获取文件锁超时: {path}");
        }

        try
        {
            File.Delete(fullPath);
            _logger?.LogDebug("删除文件成功: {Path}", path);

            // 发送变更通知
            _changes.OnNext(new FileChangeEventArgs(path, FileChangeType.Deleted));
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    /// <summary>
    /// 列出所有文件
    /// </summary>
    public Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default)
    {
        var result = new List<FileNode>();
        var types = new[] { "session", "daily", "digest" };

        foreach (var type in types)
        {
            var typeDir = Path.Combine(_baseDirectory, type);
            if (!Directory.Exists(typeDir))
                continue;

            var searchPattern = pattern ?? "*.md";
            var files = Directory.GetFiles(typeDir, searchPattern, SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                if (filePath.EndsWith(".tmp"))
                    continue;

                try
                {
                    var relativePath = GetRelativePath(filePath);
                    var content = File.ReadAllText(filePath, Encoding.UTF8);
                    var node = CreateFileNode(relativePath, content, filePath);
                    result.Add(node);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "跳过损坏的文件: {FilePath}", filePath);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<FileNode>>(result);
    }

    /// <summary>
    /// 按前缀列出文件
    /// </summary>
    public Task<IReadOnlyList<FileNode>> ListByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        ValidatePath(prefix);

        var result = new List<FileNode>();
        var prefixDir = GetFullPath(prefix);

        if (!Directory.Exists(prefixDir))
        {
            return Task.FromResult<IReadOnlyList<FileNode>>(result);
        }

        var files = Directory.GetFiles(prefixDir, "*.md", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            if (filePath.EndsWith(".tmp"))
                continue;

            try
            {
                var relativePath = GetRelativePath(filePath);
                var content = File.ReadAllText(filePath, Encoding.UTF8);
                var node = CreateFileNode(relativePath, content, filePath);
                result.Add(node);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "跳过损坏的文件: {FilePath}", filePath);
            }
        }

        return Task.FromResult<IReadOnlyList<FileNode>>(result);
    }

    /// <summary>
    /// 批量写入
    /// </summary>
    public async Task WriteBatchAsync(IEnumerable<(string path, string content)> items, CancellationToken ct = default)
    {
        foreach (var (path, content) in items)
        {
            ct.ThrowIfCancellationRequested();
            await WriteAsync(path, content, ct);
        }
    }

    /// <summary>
    /// 获取相对路径
    /// </summary>
    private string GetRelativePath(string fullPath)
    {
        return fullPath.Substring(_baseDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 创建 FileNode (解析 YAML frontmatter)
    /// </summary>
    private FileNode CreateFileNode(string relativePath, string content, string fullPath)
    {
        var frontMatter = YamlParser.ParseYamlFrontMatter(content);
        var fileInfo = new FileInfo(fullPath);
        var modifiedTime = fileInfo.LastWriteTimeUtc;

        // 解析元数据
        var metadata = ParseMetadata(frontMatter, relativePath);

        // 解析 wikilinks
        var links = WikilinkParser.Parse(content);

        return new FileNode(
            Path: relativePath.Replace('\\', '/'),
            Content: content,
            ModifiedTime: modifiedTime,
            Metadata: metadata,
            Links: links
        );
    }

    /// <summary>
    /// 解析 YAML frontmatter 元数据
    /// </summary>
    private FileMetadata ParseMetadata(Dictionary<string, object?> frontMatter, string path)
    {
        var id = GetString(frontMatter, "id") ?? Guid.NewGuid().ToString("N")[..8];
        var typeStr = GetString(frontMatter, "type");
        var title = GetString(frontMatter, "title");
        var tags = GetStringList(frontMatter, "tags") ?? Array.Empty<string>();
        var importance = GetDouble(frontMatter, "importance") ?? 0.5;
        var confidence = GetDouble(frontMatter, "confidence") ?? 1.0;
        var createdAt = GetDateTime(frontMatter, "createdAt") ?? DateTimeOffset.UtcNow;
        var expiresAt = GetDateTime(frontMatter, "expiresAt");

        // 解析类型
        var type = InferType(path);
        if (typeStr != null && Enum.TryParse<ReMeMemoryType>(typeStr, ignoreCase: true, out var parsedType))
        {
            type = parsedType;
        }

        return new FileMetadata(
            Id: id,
            Type: type,
            Title: title,
            Tags: tags,
            Importance: importance,
            Confidence: confidence,
            CreatedAt: createdAt,
            ExpiresAt: expiresAt
        );
    }

    private static ReMeMemoryType InferType(string path)
    {
        if (path.StartsWith("session/")) return ReMeMemoryType.Session;
        if (path.StartsWith("daily/")) return ReMeMemoryType.Daily;
        if (path.StartsWith("digest/")) return ReMeMemoryType.Digest;
        return ReMeMemoryType.Daily;
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;
        return value.ToString();
    }

    private static IReadOnlyList<string>? GetStringList(Dictionary<string, object?> dict, string key)
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

    private static double? GetDouble(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is double d)
            return d;

        if (value is float f)
            return f;

        if (value is int i)
            return i;

        if (double.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static DateTimeOffset? GetDateTime(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is DateTimeOffset dto)
            return dto;

        if (value is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);

        if (value is long ms)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);

        if (long.TryParse(value.ToString(), out var parsedMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(parsedMs);

        if (DateTimeOffset.TryParse(value.ToString(), out var parsedDto))
            return parsedDto;

        return null;
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Dispose()
    {
        _changes.Dispose();

        lock (_lockDictLock)
        {
            foreach (var fileLock in _fileLocks.Values)
            {
                fileLock.Dispose();
            }
            _fileLocks.Clear();
        }
    }
}
