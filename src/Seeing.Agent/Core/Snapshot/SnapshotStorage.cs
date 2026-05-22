using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 快照存储
    /// </summary>
    public class SnapshotStorage
    {
        private readonly ILogger<SnapshotStorage> _logger;
        private readonly SnapshotOptions _options;
        private readonly ConcurrentDictionary<string, string> _contentCache = new();

        public SnapshotStorage(ILogger<SnapshotStorage> logger, IOptions<SnapshotOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        /// <summary>保存快照元数据</summary>
        public async Task SaveMetadataAsync(Snapshot snapshot, CancellationToken ct = default)
        {
            var path = GetMetadataPath(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            
            var json = JsonSerializer.Serialize(snapshot);
            await File.WriteAllTextAsync(path, json, ct);
        }

        /// <summary>加载快照元数据</summary>
        public async Task<Snapshot?> LoadMetadataAsync(string snapshotId, CancellationToken ct = default)
        {
            var path = GetMetadataPathById(snapshotId);
            if (!File.Exists(path)) return null;

            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                return JsonSerializer.Deserialize<Snapshot>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load snapshot metadata: {SnapshotId}", snapshotId);
                return null;
            }
        }

        /// <summary>保存完整内容</summary>
        public async Task SaveContentAsync(string snapshotId, string content, string sessionId, CancellationToken ct = default)
        {
            var path = GetContentPath(snapshotId, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            
            await File.WriteAllTextAsync(path, content, ct);
            _contentCache[snapshotId] = content;
        }

        /// <summary>加载完整内容</summary>
        public async Task<string?> LoadContentAsync(string snapshotId, string sessionId, CancellationToken ct = default)
        {
            if (_contentCache.TryGetValue(snapshotId, out var cached))
                return cached;

            var path = GetContentPath(snapshotId, sessionId);
            if (!File.Exists(path)) return null;

            var content = await File.ReadAllTextAsync(path, ct);
            _contentCache[snapshotId] = content;
            return content;
        }

        /// <summary>删除快照</summary>
        public Task DeleteSnapshotAsync(string snapshotId, string sessionId, CancellationToken ct = default)
        {
            var metaPath = GetMetadataPathById(snapshotId);
            if (File.Exists(metaPath)) File.Delete(metaPath);

            var contentPath = GetContentPath(snapshotId, sessionId);
            if (File.Exists(contentPath)) File.Delete(contentPath);

            _contentCache.TryRemove(snapshotId, out _);
            return Task.CompletedTask;
        }

        /// <summary>列出会话的所有快照</summary>
        public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string sessionId, CancellationToken ct = default)
        {
            var sessionPath = Path.Combine(_options.StoragePath, sessionId, "meta");
            if (!Directory.Exists(sessionPath)) return new List<Snapshot>();

            var result = new List<Snapshot>();
            foreach (var file in Directory.GetFiles(sessionPath, "*.json"))
            {
                var snapshot = await LoadMetadataAsync(Path.GetFileNameWithoutExtension(file), ct);
                if (snapshot != null) result.Add(snapshot);
            }

            return result;
        }

        /// <summary>清理过期快照</summary>
        public async Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken ct = default)
        {
            var count = 0;
            var cutoff = DateTimeOffset.UtcNow - maxAge;

            foreach (var sessionDir in Directory.GetDirectories(_options.StoragePath))
            {
                var metaDir = Path.Combine(sessionDir, "meta");
                if (!Directory.Exists(metaDir)) continue;

                foreach (var file in Directory.GetFiles(metaDir, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, ct);
                        var snapshot = JsonSerializer.Deserialize<Snapshot>(json);
                        
                        if (snapshot != null && snapshot.CreatedAt < cutoff)
                        {
                            await DeleteSnapshotAsync(snapshot.Id, Path.GetFileName(sessionDir), ct);
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process snapshot file: {File}", file);
                    }
                }
            }

            return count;
        }

        private string GetMetadataPath(Snapshot snapshot)
            => Path.Combine(_options.StoragePath, snapshot.SessionId, "meta", $"{snapshot.Id}.json");

        private string GetMetadataPathById(string snapshotId)
            => Path.Combine(_options.StoragePath, "*", "meta", $"{snapshotId}.json");

        private string GetContentPath(string snapshotId, string sessionId)
            => Path.Combine(_options.StoragePath, sessionId, "content", $"{snapshotId}.txt");

        public static string ComputeHash(string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
