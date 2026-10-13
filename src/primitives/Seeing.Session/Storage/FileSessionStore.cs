using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 基于文件系统的会话存储实现
    /// 使用 JSON 文件存储会话数据，兼容 WebUI 的 ~/.seeing/sessions/*.json 格式
    /// </summary>
    public class FileSessionStore : ISessionStore, IRelocatableSessionStore, ISessionSummaryProvider, ISessionArchiveStore, IDisposable
    {
        private volatile string _baseDirectory;
        private readonly ILogger<FileSessionStore>? _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>会话引用资源目录后缀（工具输出落盘目录约定）</summary>
        private const string RefDirectorySuffix = ".ref";

        /// <summary>归档目录名（位于基础目录下）</summary>
        private const string ArchiveDirectoryName = "_archive";

        // 文件锁字典（实例级别，随实例生命周期释放）
        private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
        private readonly object _lockDictLock = new();
        private bool _disposed;

        /// <summary>
        /// 创建 FileSessionStore 实例
        /// </summary>
        /// <param name="baseDirectory">基础目录路径，默认为 ~/.seeing/sessions</param>
        /// <param name="logger">日志记录器</param>
        public FileSessionStore(string? baseDirectory = null, ILogger<FileSessionStore>? logger = null)
        {
            _logger = logger;
            _baseDirectory = baseDirectory ?? GetDefaultSessionDirectory();
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            EnsureDirectoryExists();
        }

        /// <summary>当前会话存储基础目录</summary>
        public string? BaseDirectory => _baseDirectory;

        /// <inheritdoc/>
        public void SetBaseDirectory(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException("基础目录不能为空", nameof(baseDirectory));

            _baseDirectory = baseDirectory;
            EnsureDirectoryExists();
            _logger?.LogInformation("会话存储目录已切换: {Directory}", _baseDirectory);
        }

        /// <summary>
        /// 获取默认会话目录路径 (~/.seeing/sessions)
        /// </summary>
        public static string GetDefaultSessionDirectory()
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".seeing", "sessions");
        }

        /// <summary>
        /// 确保目录存在
        /// </summary>
        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
                _logger?.LogInformation("创建会话存储目录: {Directory}", _baseDirectory);
            }
        }

        /// <summary>
        /// 获取会话文件路径
        /// </summary>
        private string GetSessionFilePath(string sessionId)
        {
            ValidateSessionId(sessionId);
            return Path.Combine(_baseDirectory, $"{sessionId}.json");
        }

        /// <summary>
        /// 验证会话ID，防止路径遍历攻击
        /// </summary>
        private static void ValidateSessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }

            // 检查路径遍历攻击（防止访问上级目录）
            if (sessionId.Contains("..") || sessionId.Contains("/") || sessionId.Contains("\\"))
            {
                throw new ArgumentException("会话ID包含非法路径字符", nameof(sessionId));
            }

            // 检查非法文件名字符
            var invalidChars = Path.GetInvalidFileNameChars();
            if (sessionId.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException("会话ID包含非法字符", nameof(sessionId));
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
        /// 保存单个会话（<paramref name="data"/> 为调用方移交的私有快照，本方法不修改其时间戳）。
        /// </summary>
        public async Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            ValidateSessionId(data.Id);

            var filePath = GetSessionFilePath(data.Id);
            var fileLock = GetFileLock(filePath);

            await fileLock.WaitAsync(ct);

            try
            {
                // 快照契约：时间戳由调用方设置，仅在 CreatedAt 缺省时补齐
                if (data.CreatedAt == default)
                {
                    data.CreatedAt = DateTime.Now;
                }

                var json = JsonSerializer.Serialize(data, _jsonOptions);

                // 使用临时文件 + 原子替换确保写入完整性
                var tempPath = filePath + ".tmp";
                var moved = false;
                try
                {
                    await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct);

                    // 原子替换（带重试机制处理 Windows Defender/OS 缓存导致的瞬时访问失败）
                    await AtomicMoveWithRetryAsync(tempPath, filePath, data.Id, ct);
                    moved = true;
                }
                finally
                {
                    // 仅在成功替换后清理残留 .tmp；失败时保留唯一可恢复副本供排查/恢复
                    if (moved)
                        TryDeleteFile(tempPath);
                }

                _logger?.LogDebug("保存会话成功: {SessionId}", data.Id);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 原子文件替换（带重试机制）
        /// </summary>
        /// <remarks>
        /// Windows 上 File.Move 可能因以下原因失败：
        /// 1. Windows Defender 扫描新写入的文件
        /// 2. 操作系统文件系统缓存未完全刷新
        /// 3. 其他进程短暂持有文件句柄
        /// 重试机制可处理这些瞬时故障。
        /// </remarks>
        private async Task AtomicMoveWithRetryAsync(string sourcePath, string destPath, string sessionId, CancellationToken ct)
        {
            const int maxRetries = 5;
            const int initialDelayMs = 50;
            var delay = initialDelayMs;

            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    // 确保源文件存在
                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException($"临时文件不存在: {sourcePath}");
                    }

                    // 尝试原子替换
                    File.Move(sourcePath, destPath, overwrite: true);
                    return; // 成功则返回
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger?.LogWarning(
                        "文件移动失败 (尝试 {Attempt}/{MaxRetries}): {SessionId}, 错误: {Error}",
                        attempt + 1, maxRetries, sessionId, ex.Message);

                    // 最后一次尝试失败不重试，落到下方备用策略
                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(delay, ct);
                        delay *= 2;
                    }
                }
                catch (IOException ex)
                {
                    // 处理 IO 异常（如文件被锁定）
                    _logger?.LogWarning(
                        "文件移动失败 (尝试 {Attempt}/{MaxRetries}): {SessionId}, 错误: {Error}",
                        attempt + 1, maxRetries, sessionId, ex.Message);

                    if (attempt < maxRetries - 1)
                    {
                        await Task.Delay(delay, ct);
                        delay *= 2;
                    }
                }
            }

            // 所有重试都失败，尝试最后的安全备份策略：Copy 覆盖目标后删除临时源。
            // 采用 Copy-then-delete（而非 delete dest + Move）：即使目标无法替换，
            // 也不会先毁掉已有会话文件；Copy 失败时保留 source 作为唯一可恢复副本。
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                TryDeleteFile(sourcePath);
                _logger?.LogWarning("使用备用策略成功保存会话: {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                // 不删除 source：交由 SaveAsync 的 finally 按成功/失败决定清理
                _logger?.LogError(ex, "保存会话失败（所有重试耗尽）: {SessionId}", sessionId);
                throw new IOException($"保存会话失败: {sessionId}", ex);
            }
        }

        /// <summary>尽力删除文件（不存在或失败均静默）。</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 忽略清理失败
            }
        }

        /// <summary>
        /// 加载单个会话
        /// </summary>
        public async Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            ValidateSessionId(sessionId);

            var filePath = GetSessionFilePath(sessionId);

            if (!File.Exists(filePath))
            {
                _logger?.LogDebug("会话文件不存在: {FilePath}", filePath);
                return null;
            }

            var fileLock = GetFileLock(filePath);

            await fileLock.WaitAsync(ct);

            try
            {
                return await ReadSessionFileAsync(filePath, sessionId, ct);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 读取会话文件（内部方法）
        /// </summary>
        private async Task<SessionData?> ReadSessionFileAsync(string filePath, string sessionId, CancellationToken ct)
        {
            try
            {
                // 使用 FileStream 读取，支持文件共享读取
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(ct);

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.LogWarning("会话文件为空: {SessionId}", sessionId);
                    throw new SessionLoadException(
                        sessionId,
                        filePath,
                        "会话文件为空");
                }

                var data = JsonSerializer.Deserialize<SessionData>(json, _jsonOptions);

                if (data == null)
                {
                    _logger?.LogWarning("会话数据反序列化失败: {SessionId}", sessionId);
                    throw new SessionLoadException(
                        sessionId,
                        filePath,
                        "会话数据反序列化失败");
                }

                return data;
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "会话文件格式错误: {SessionId}", sessionId);
                throw new SessionLoadException(
                    sessionId,
                    filePath,
                    "会话文件格式错误，可能已损坏",
                    ex);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "读取会话文件失败: {SessionId}", sessionId);
                throw new SessionLoadException(
                    sessionId,
                    filePath,
                    "读取会话文件失败",
                    ex);
            }
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            ValidateSessionId(sessionId);

            DeleteRefDirectory(sessionId);

            var filePath = GetSessionFilePath(sessionId);

            if (!File.Exists(filePath))
            {
                _logger?.LogDebug("会话文件不存在，无需删除: {FilePath}", filePath);
                return;
            }

            var fileLock = GetFileLock(filePath);

            await fileLock.WaitAsync(ct);

            try
            {
                File.Delete(filePath);
                _logger?.LogDebug("删除会话成功: {SessionId}", sessionId);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 删除会话引用资源目录（{sessionId}.ref/），用于工具输出落盘等附属资源。
        /// 目录不存在或删除失败时仅记录日志，不影响主流程。
        /// </summary>
        private void DeleteRefDirectory(string sessionId)
        {
            var refDir = Path.Combine(_baseDirectory, sessionId + RefDirectorySuffix);
            try
            {
                if (Directory.Exists(refDir))
                {
                    Directory.Delete(refDir, recursive: true);
                    _logger?.LogDebug("删除会话引用目录成功: {RefDir}", refDir);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "删除会话引用目录失败: {SessionId}, {RefDir}", sessionId, refDir);
            }
        }

        /// <summary>
        /// 列出所有会话
        /// </summary>
        public IAsyncEnumerable<SessionData> ListAsync(CancellationToken ct = default) =>
            EnumerateSessions(ct);

        /// <summary>
        /// 枚举所有会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessions([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_baseDirectory))
            {
                yield break;
            }

            foreach (var filePath in Directory.GetFiles(_baseDirectory, "*.json"))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                SessionData? data = null;

                try
                {
                    var fileLock = GetFileLock(filePath);
                    await fileLock.WaitAsync(cancellationToken);
                    try
                    {
                        data = await ReadSessionFileAsync(filePath, fileName, cancellationToken);
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
                catch (SessionLoadException ex)
                {
                    _logger?.LogWarning(ex, "跳过损坏的会话文件: {FilePath}", filePath);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "读取会话文件失败: {FilePath}", filePath);
                    continue;
                }

                if (data != null)
                {
                    yield return data;
                }
            }
        }

        /// <summary>
        /// 按分区和代理查询会话
        /// </summary>
        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default) =>
            EnumerateSessionsFiltered(partitionId, agentId, ct);

        /// <summary>
        /// 枚举过滤后的会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessionsFiltered(
            string partitionId,
            string agentId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var session in EnumerateSessions(cancellationToken))
            {
                var matchPartition = string.IsNullOrEmpty(partitionId) ||
                                     session.PartitionId == partitionId;

                var matchAgent = string.IsNullOrEmpty(agentId) ||
                                 (session.SelectedAgent == agentId);

                if (matchPartition && matchAgent)
                {
                    yield return session;
                }
            }
        }

        /// <summary>
        /// 批量保存会话
        /// </summary>
        public async Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            foreach (var session in data)
            {
                await SaveAsync(session, ct);
            }
        }

        /// <summary>
        /// 批量加载会话
        /// </summary>
        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) =>
            ListAsync(ct);

        // ============================ 摘要端口 ============================

        /// <inheritdoc/>
        public IAsyncEnumerable<SessionSummary> ListSummariesAsync(CancellationToken ct = default) =>
            EnumerateSummaries(ct);

        /// <summary>
        /// 枚举活跃会话摘要：流式只读顶层字段，跳过 messages 数组，不构造消息对象。
        /// </summary>
        private async IAsyncEnumerable<SessionSummary> EnumerateSummaries(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!Directory.Exists(_baseDirectory))
                yield break;

            foreach (var filePath in Directory.GetFiles(_baseDirectory, "*.json"))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                SessionSummary? summary = null;

                try
                {
                    var fileLock = GetFileLock(filePath);
                    await fileLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        summary = await ReadSummaryAsync(filePath, fileName, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        fileLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "读取会话摘要失败: {FilePath}", filePath);
                    continue;
                }

                if (summary != null)
                    yield return summary;
            }
        }

        /// <summary>流式读取会话文件顶层字段生成摘要；损坏文件返回 null。</summary>
        private async Task<SessionSummary?> ReadSummaryAsync(string filePath, string sessionId, CancellationToken ct)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
                return ParseSummary(bytes, sessionId, archived: false);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "会话摘要 JSON 格式错误: {FilePath}", filePath);
                return null;
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "读取会话摘要 IO 失败: {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// 用 <see cref="Utf8JsonReader"/> 流式解析顶层字段生成摘要：
        /// 跳过 <c>messages</c> 数组体（不构建 DOM、不构造 <see cref="SessionMessage"/>），仅统计其元素个数。
        /// </summary>
        private static SessionSummary? ParseSummary(ReadOnlySpan<byte> utf8, string fallbackId, bool archived)
        {
            // 跳过 UTF-8 BOM（Encoding.UTF8 写入时可能带 BOM；Utf8JsonReader 不自动跳过）
            if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
                utf8 = utf8.Slice(3);

            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            string? id = null, title = null, partitionId = null, selectedAgent = null, selectedModel = null;
            string? groupId = null, channelId = null, userId = null, workingDirectory = null, scenario = null;
            var status = SessionStatus.Created;
            var kind = SessionKind.Root;
            var autoApprove = SessionAutoApprove.FollowGlobal;
            var isArchived = false;
            var messageCount = 0;
            DateTime createdAt = default, updatedAt = default, lastActiveAt = default;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                var name = reader.GetString();
                if (!reader.Read())
                    break;

                switch (name)
                {
                    case "id": id = ReadString(ref reader); break;
                    case "title": title = ReadString(ref reader); break;
                    case "partitionId": partitionId = ReadString(ref reader); break;
                    case "selectedAgent": selectedAgent = ReadString(ref reader); break;
                    case "selectedModel": selectedModel = ReadString(ref reader); break;
                    case "groupId": groupId = ReadString(ref reader); break;
                    case "channelId": channelId = ReadString(ref reader); break;
                    case "userId": userId = ReadString(ref reader); break;
                    case "workingDirectory": workingDirectory = ReadString(ref reader); break;
                    case "scenario": scenario = ReadString(ref reader); break;
                    case "status": status = ReadEnum(ref reader, SessionStatus.Created); break;
                    case "kind": kind = ReadEnum(ref reader, SessionKind.Root); break;
                    case "autoApprove": autoApprove = ReadEnum(ref reader, SessionAutoApprove.FollowGlobal); break;
                    case "isArchived": isArchived = reader.TokenType == JsonTokenType.True; break;
                    case "createdAt": createdAt = ReadDateTime(ref reader); break;
                    case "updatedAt": updatedAt = ReadDateTime(ref reader); break;
                    case "lastActiveAt": lastActiveAt = ReadDateTime(ref reader); break;
                    case "messages": messageCount = CountArrayElements(ref reader); break;
                    default: reader.Skip(); break;
                }
            }

            return new SessionSummary
            {
                Id = id ?? fallbackId,
                Title = title ?? string.Empty,
                PartitionId = partitionId,
                SelectedAgent = selectedAgent,
                SelectedModel = selectedModel,
                Status = archived ? SessionStatus.Archived : status,
                Kind = kind,
                GroupId = groupId,
                IsArchived = archived || isArchived,
                MessageCount = messageCount,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                LastActiveAt = lastActiveAt,
                ChannelId = channelId,
                UserId = userId,
                WorkingDirectory = workingDirectory,
                Scenario = scenario,
                AutoApprove = autoApprove
            };
        }

        private static string? ReadString(ref Utf8JsonReader reader) =>
            reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        private static DateTime ReadDateTime(ref Utf8JsonReader reader) =>
            reader.TokenType == JsonTokenType.String
            && DateTime.TryParse(reader.GetString(), out var dt)
                ? dt
                : default;

        private static TEnum ReadEnum<TEnum>(ref Utf8JsonReader reader, TEnum fallback) where TEnum : struct, Enum
        {
            var underlying = Enum.GetUnderlyingType(typeof(TEnum));

            if (reader.TokenType == JsonTokenType.Number)
            {
                object? numeric = underlying == typeof(long)
                    ? reader.TryGetInt64(out var l) ? l : null
                    : reader.TryGetInt32(out var i) ? i : null;

                if (numeric != null)
                {
                    var boxed = Enum.ToObject(typeof(TEnum), numeric);
                    return (TEnum)boxed;
                }
            }

            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed))
                return parsed;

            return fallback;
        }

        /// <summary>当前 reader 位于数组起始处；遍历统计顶层元素个数（不分配）。</summary>
        private static int CountArrayElements(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                return 0;

            var depth = 1;
            var count = 0;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (depth == 1) count++;
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        if (depth == 0) return count;
                        break;
                    default:
                        if (depth == 1) count++;
                        break;
                }
            }

            return count;
        }

        // ============================ 归档端口 ============================

        /// <summary>归档目录路径（基础目录下 _archive）。</summary>
        private string GetArchiveDirectory() => Path.Combine(_baseDirectory, ArchiveDirectoryName);

        /// <inheritdoc/>
        public async Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default)
        {
            ValidateSessionId(sessionId);

            var filePath = GetSessionFilePath(sessionId);
            if (!File.Exists(filePath))
            {
                _logger?.LogDebug("会话文件不存在，无需归档: {SessionId}", sessionId);
                return false;
            }

            var fileLock = GetFileLock(filePath);
            await fileLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 读取全量并标记归档
                var data = await ReadSessionFileAsync(filePath, sessionId, ct).ConfigureAwait(false)
                    ?? throw new SessionLoadException(sessionId, filePath, "归档前读取会话失败");

                data.Metadata[SessionMetadataKeys.PreArchiveStatus] = ((int)data.Status).ToString();
                data.IsArchived = true;
                data.ArchivedAt = DateTimeOffset.Now;
                data.Status = SessionStatus.Archived;

                var json = JsonSerializer.Serialize(data, _jsonOptions);

                var archiveDir = GetArchiveDirectory();
                Directory.CreateDirectory(archiveDir);
                var archivePath = Path.Combine(archiveDir, $"{sessionId}.json.gz");

                // 临时文件 + 原子替换，避免并发列表读到半写的 .gz
                var archiveTemp = archivePath + ".tmp";
                await using (var fileStream = File.Create(archiveTemp))
                await using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
                await using (var writer = new StreamWriter(gzipStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                }
                File.Move(archiveTemp, archivePath, overwrite: true);

                // 从活跃目录移除（含 ref 附属目录）
                File.Delete(filePath);
                DeleteRefDirectory(sessionId);

                _logger?.LogInformation("归档会话: {SessionId} -> {ArchivePath}", sessionId, archivePath);
                return true;
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default)
        {
            ValidateSessionId(sessionId);

            var archivePath = Path.Combine(GetArchiveDirectory(), $"{sessionId}.json.gz");
            if (!File.Exists(archivePath))
            {
                _logger?.LogDebug("归档文件不存在: {SessionId}", sessionId);
                return false;
            }

            var filePath = GetSessionFilePath(sessionId);
            var fileLock = GetFileLock(filePath);
            await fileLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                SessionData? data;
                await using (var fileStream = File.OpenRead(archivePath))
                await using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
                {
                    var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    data = JsonSerializer.Deserialize<SessionData>(json, _jsonOptions);
                }

                if (data == null)
                {
                    _logger?.LogWarning("归档文件反序列化失败: {SessionId}", sessionId);
                    return false;
                }

                data.IsArchived = false;
                data.ArchivedAt = null;
                // 还原归档前原始状态（缺失时回退 Active），并刷新活跃时间避免立即再次被扫描归档
                data.Status = data.Metadata.TryGetValue(SessionMetadataKeys.PreArchiveStatus, out var preStatus)
                    && int.TryParse(preStatus, out var preStatusValue)
                    && Enum.IsDefined(typeof(SessionStatus), preStatusValue)
                        ? (SessionStatus)preStatusValue
                        : SessionStatus.Active;
                data.Metadata.Remove(SessionMetadataKeys.PreArchiveStatus);
                data.UpdatedAt = DateTime.Now;
                data.LastActiveAt = DateTime.Now;

                // 复用原子写路径（临时文件 + 原子替换），避免半写文件
                var json2 = JsonSerializer.Serialize(data, _jsonOptions);
                var tempPath = filePath + ".tmp";
                var moved = false;
                try
                {
                    await File.WriteAllTextAsync(tempPath, json2, Encoding.UTF8, ct).ConfigureAwait(false);
                    await AtomicMoveWithRetryAsync(tempPath, filePath, sessionId, ct).ConfigureAwait(false);
                    moved = true;
                }
                finally
                {
                    if (moved)
                        TryDeleteFile(tempPath);
                }

                File.Delete(archivePath);

                _logger?.LogInformation("恢复归档会话: {SessionId}", sessionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "恢复归档会话失败: {SessionId}", sessionId);
                return false;
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<SessionSummary> ListArchivedAsync(CancellationToken ct = default) =>
            EnumerateArchivedSummaries(ct);

        /// <summary>枚举归档目录下的会话摘要。</summary>
        private async IAsyncEnumerable<SessionSummary> EnumerateArchivedSummaries(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var archiveDir = GetArchiveDirectory();
            if (!Directory.Exists(archiveDir))
                yield break;

            foreach (var archivePath in Directory.GetFiles(archiveDir, "*.json.gz"))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(archivePath));
                SessionSummary? summary = null;
                try
                {
                    summary = await ReadArchivedSummaryAsync(archivePath, fileName, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "读取归档摘要失败: {FilePath}", archivePath);
                    continue;
                }

                if (summary != null)
                    yield return summary;
            }
        }

        private async Task<SessionSummary?> ReadArchivedSummaryAsync(string archivePath, string sessionId, CancellationToken ct)
        {
            await using var fileStream = File.OpenRead(archivePath);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await gzipStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ParseSummary(ms.GetBuffer().AsSpan(0, (int)ms.Length), sessionId, archived: true);
        }

        /// <summary>
        /// 释放文件锁字典中的所有信号量
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lockDictLock)
            {
                foreach (var semaphore in _fileLocks.Values)
                {
                    semaphore.Dispose();
                }
                _fileLocks.Clear();
            }
        }
    }
}