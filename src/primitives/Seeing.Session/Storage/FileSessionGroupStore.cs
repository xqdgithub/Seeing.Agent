using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 基于文件系统的会话组存储：每个组一个 JSON 文件（<c>{groupId}.json</c>）。
    /// <para>风格与 <see cref="FileSessionStore"/> 保持一致（CamelCase、缩进、大小写不敏感）。</para>
    /// </summary>
    public class FileSessionGroupStore : IRelocatableSessionGroupStore
    {
        private volatile string _baseDirectory;
        private readonly ILogger<FileSessionGroupStore>? _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        // 按组文件的锁字典（实例级别，消除全局写锁串行）
        private readonly Dictionary<string, SemaphoreSlim> _groupLocks = new();
        private readonly object _lockDictLock = new();

        /// <summary>创建会话组存储。</summary>
        /// <param name="baseDirectory">基础目录，默认 ~/.seeing/session-groups</param>
        /// <param name="logger">日志记录器</param>
        public FileSessionGroupStore(
            string? baseDirectory = null,
            ILogger<FileSessionGroupStore>? logger = null)
        {
            _logger = logger;
            _baseDirectory = baseDirectory ?? GetDefaultDirectory();
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            EnsureDirectoryExists();
        }

        /// <inheritdoc/>
        public string BaseDirectory => _baseDirectory;

        /// <inheritdoc/>
        public void SetBaseDirectory(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException("基础目录不能为空", nameof(baseDirectory));

            _baseDirectory = baseDirectory;
            EnsureDirectoryExists();
            _logger?.LogInformation("会话组存储目录已切换: {Directory}", _baseDirectory);
        }

        /// <summary>默认会话组目录（~/.seeing/session-groups）。</summary>
        public static string GetDefaultDirectory()
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".seeing", "session-groups");
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
                _logger?.LogInformation("创建会话组存储目录: {Directory}", _baseDirectory);
            }
        }

        /// <inheritdoc/>
        public async Task<SessionGroup?> LoadAsync(string groupId, CancellationToken ct = default)
        {
            var filePath = GetGroupFilePath(groupId);
            if (!File.Exists(filePath))
                return null;

            return await ReadGroupFileAsync(filePath, groupId, ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<SessionGroup?> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            await foreach (var group in ListAsync(ct))
            {
                if (group.Members.Any(m => m.SessionId == sessionId))
                    return group;
            }

            return null;
        }

        /// <inheritdoc/>
        public async Task SaveAsync(SessionGroup group, CancellationToken ct = default)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            var filePath = GetGroupFilePath(group.Id);
            var groupLock = GetGroupLock(filePath);

            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 快照契约：时间戳由调用方设置，仅在 CreatedAt 缺省时补齐
                if (group.CreatedAt == default)
                    group.CreatedAt = DateTime.Now;

                var json = JsonSerializer.Serialize(group, _jsonOptions);
                var tempPath = filePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
                File.Move(tempPath, filePath, overwrite: true);

                _logger?.LogDebug("保存会话组成功: {GroupId}", group.Id);
            }
            finally
            {
                groupLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(string groupId, CancellationToken ct = default)
        {
            var filePath = GetGroupFilePath(groupId);
            var groupLock = GetGroupLock(filePath);

            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger?.LogDebug("删除会话组成功: {GroupId}", groupId);
                }
            }
            finally
            {
                groupLock.Release();
            }
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<SessionGroup> ListAsync(CancellationToken ct = default) => EnumerateAsync(ct);

        private async IAsyncEnumerable<SessionGroup> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (!Directory.Exists(_baseDirectory))
                yield break;

            foreach (var filePath in Directory.GetFiles(_baseDirectory, "*.json"))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var groupId = Path.GetFileNameWithoutExtension(filePath);
                SessionGroup? group = null;
                try
                {
                    group = await ReadGroupFileAsync(filePath, groupId, ct).ConfigureAwait(false);
                }
                catch (SessionLoadException ex)
                {
                    _logger?.LogWarning(ex, "跳过损坏的会话组文件: {FilePath}", filePath);
                }

                if (group != null)
                    yield return group;
            }
        }

        private async Task<SessionGroup?> ReadGroupFileAsync(string filePath, string groupId, CancellationToken ct)
        {
            try
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var group = JsonSerializer.Deserialize<SessionGroup>(json, _jsonOptions);
                if (group == null)
                    throw new SessionLoadException(groupId, filePath, "会话组数据反序列化失败");

                return group;
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "会话组文件格式错误: {GroupId}", groupId);
                throw new SessionLoadException(groupId, filePath, "会话组文件格式错误，可能已损坏", ex);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "读取会话组文件失败: {GroupId}", groupId);
                throw new SessionLoadException(groupId, filePath, "读取会话组文件失败", ex);
            }
        }

        private SemaphoreSlim GetGroupLock(string filePath)
        {
            lock (_lockDictLock)
            {
                if (!_groupLocks.TryGetValue(filePath, out var groupLock))
                {
                    groupLock = new SemaphoreSlim(1, 1);
                    _groupLocks[filePath] = groupLock;
                }
                return groupLock;
            }
        }

        private string GetGroupFilePath(string groupId)
        {
            ValidateGroupId(groupId);
            return Path.Combine(_baseDirectory, $"{groupId}.json");
        }

        private static void ValidateGroupId(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
                throw new ArgumentException("会话组ID不能为空", nameof(groupId));

            if (groupId.Contains("..") || groupId.Contains('/') || groupId.Contains('\\'))
                throw new ArgumentException("会话组ID包含非法路径字符", nameof(groupId));

            if (groupId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("会话组ID包含非法字符", nameof(groupId));
        }
    }
}
