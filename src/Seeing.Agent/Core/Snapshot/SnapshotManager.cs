using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 快照管理器实现
    /// </summary>
    public class SnapshotManager : ISnapshotManager
    {
        private readonly ILogger<SnapshotManager> _logger;
        private readonly SnapshotStorage _storage;
        private readonly DiffCalculator _diffCalculator;
        private readonly SnapshotOptions _options;
        private readonly ConcurrentDictionary<string, Snapshot> _snapshotCache = new();

        public SnapshotManager(
            ILogger<SnapshotManager> logger,
            SnapshotStorage storage,
            DiffCalculator diffCalculator,
            IOptions<SnapshotOptions> options)
        {
            _logger = logger;
            _storage = storage;
            _diffCalculator = diffCalculator;
            _options = options.Value;
        }

        public async Task<Snapshot> CreateSnapshotAsync(
            string filePath,
            string sessionId,
            string? label = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var hash = SnapshotStorage.ComputeHash(content);

            // 查找同一文件的最后一个快照
            var snapshots = await _storage.ListSnapshotsAsync(sessionId, cancellationToken);
            var lastSnapshot = snapshots
                .Where(s => s.FilePath == filePath)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();

            // 如果内容未变，返回已有快照
            if (lastSnapshot != null && lastSnapshot.ContentHash == hash)
            {
                _logger.LogDebug("Content unchanged, returning existing snapshot: {SnapshotId}", lastSnapshot.Id);
                return lastSnapshot;
            }

            Snapshot snapshot;
            
            if (lastSnapshot != null)
            {
                // Diff 模式：存储差异
                var lastContent = await GetSnapshotContentAsync(lastSnapshot.Id, cancellationToken);
                var diffs = _diffCalculator.ComputeDiff(lastContent, content);
                var patch = _diffCalculator.SerializePatch(diffs);

                snapshot = new Snapshot
                {
                    FilePath = filePath,
                    SessionId = sessionId,
                    Label = label,
                    ContentHash = hash,
                    FileSize = content.Length,
                    BaseSnapshotId = lastSnapshot.Id,
                    DiffPatches = patch
                };
            }
            else
            {
                // 完整模式：存储全部内容
                snapshot = new Snapshot
                {
                    FilePath = filePath,
                    SessionId = sessionId,
                    Label = label,
                    ContentHash = hash,
                    FileSize = content.Length
                };
            }

            await _storage.SaveMetadataAsync(snapshot, cancellationToken);
            
            if (snapshot.IsFullSnapshot)
            {
                await _storage.SaveContentAsync(snapshot.Id, content, sessionId, cancellationToken);
            }

            _snapshotCache[snapshot.Id] = snapshot;
            _logger.LogInformation("Created snapshot {SnapshotId} for {FilePath}", snapshot.Id, filePath);

            return snapshot;
        }

        public async Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(
            string filePath,
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            var result = new List<Snapshot>();

            if (sessionId != null)
            {
                var snapshots = await _storage.ListSnapshotsAsync(sessionId, cancellationToken);
                result.AddRange(snapshots.Where(s => s.FilePath == filePath || string.IsNullOrEmpty(filePath)));
            }
            else
            {
                // 扫描所有会话
                foreach (var dir in Directory.GetDirectories(_options.StoragePath))
                {
                    var sid = Path.GetFileName(dir);
                    var snapshots = await _storage.ListSnapshotsAsync(sid, cancellationToken);
                    result.AddRange(snapshots.Where(s => s.FilePath == filePath || string.IsNullOrEmpty(filePath)));
                }
            }

            return result.OrderByDescending(s => s.CreatedAt).ToList();
        }

        public async Task<SnapshotDiff> ComputeDiffAsync(
            string snapshotId1,
            string snapshotId2,
            CancellationToken cancellationToken = default)
        {
            var content1 = await GetSnapshotContentAsync(snapshotId1, cancellationToken);
            var content2 = await GetSnapshotContentAsync(snapshotId2, cancellationToken);

            var diffs = _diffCalculator.ComputeDiff(content1, content2);
            
            return new SnapshotDiff
            {
                SnapshotId1 = snapshotId1,
                SnapshotId2 = snapshotId2,
                AddedLines = diffs.Count(d => d.Operation == DiffOperation.Insert),
                DeletedLines = diffs.Count(d => d.Operation == DiffOperation.Delete),
                UnchangedLines = diffs.Count(d => d.Operation == DiffOperation.Equal),
                UnifiedDiff = _diffCalculator.ToUnifiedDiff("", content1, content2)
            };
        }

        public async Task<SnapshotDiff> ComputeDiffWithCurrentAsync(
            string snapshotId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await GetSnapshotById(snapshotId, cancellationToken);
            if (snapshot == null)
                throw new InvalidOperationException($"Snapshot not found: {snapshotId}");

            var snapshotContent = await GetSnapshotContentAsync(snapshotId, cancellationToken);
            var currentContent = File.Exists(snapshot.FilePath)
                ? await File.ReadAllTextAsync(snapshot.FilePath, cancellationToken)
                : "";

            var diffs = _diffCalculator.ComputeDiff(snapshotContent, currentContent);

            return new SnapshotDiff
            {
                SnapshotId1 = snapshotId,
                SnapshotId2 = "current",
                AddedLines = diffs.Count(d => d.Operation == DiffOperation.Insert),
                DeletedLines = diffs.Count(d => d.Operation == DiffOperation.Delete),
                UnchangedLines = diffs.Count(d => d.Operation == DiffOperation.Equal),
                UnifiedDiff = _diffCalculator.ToUnifiedDiff(snapshot.FilePath, snapshotContent, currentContent)
            };
        }

        public async Task<bool> RestoreAsync(string snapshotId, CancellationToken cancellationToken = default)
        {
            var snapshot = await GetSnapshotById(snapshotId, cancellationToken);
            if (snapshot == null) return false;

            var content = await GetSnapshotContentAsync(snapshotId, cancellationToken);
            
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.FilePath)!);
            await File.WriteAllTextAsync(snapshot.FilePath, content, cancellationToken);

            _logger.LogInformation("Restored {FilePath} from snapshot {SnapshotId}", snapshot.FilePath, snapshotId);
            return true;
        }

        public async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
        {
            var snapshot = await GetSnapshotById(snapshotId, cancellationToken);
            if (snapshot == null) return false;

            await _storage.DeleteSnapshotAsync(snapshotId, snapshot.SessionId, cancellationToken);
            _snapshotCache.TryRemove(snapshotId, out _);

            return true;
        }

        public async Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
        {
            return await _storage.CleanupAsync(maxAge, cancellationToken);
        }

        public async Task<string> GetSnapshotContentAsync(
            string snapshotId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await GetSnapshotById(snapshotId, cancellationToken);
            if (snapshot == null)
                throw new InvalidOperationException($"Snapshot not found: {snapshotId}");

            if (snapshot.IsFullSnapshot)
            {
                return await _storage.LoadContentAsync(snapshotId, snapshot.SessionId, cancellationToken)
                    ?? throw new InvalidOperationException($"Content not found for snapshot: {snapshotId}");
            }

            // 递归获取基础快照内容并应用 Diff
            var baseContent = await GetSnapshotContentAsync(snapshot.BaseSnapshotId!, cancellationToken);
            var diffs = _diffCalculator.DeserializePatch(snapshot.DiffPatches!);
            
            return _diffCalculator.ApplyPatch(baseContent, diffs);
        }

        private async Task<Snapshot?> GetSnapshotById(string snapshotId, CancellationToken ct)
        {
            if (_snapshotCache.TryGetValue(snapshotId, out var cached))
                return cached;

            var snapshot = await _storage.LoadMetadataAsync(snapshotId, ct);
            if (snapshot != null)
                _snapshotCache[snapshotId] = snapshot;

            return snapshot;
        }
    }
}
