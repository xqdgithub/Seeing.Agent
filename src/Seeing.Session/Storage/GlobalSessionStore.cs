using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 全局 Session 存储 - 支持跨分区查询
    /// </summary>
    public class GlobalSessionStore
    {
        private readonly ILogger<GlobalSessionStore> _logger;
        private readonly string _basePath;

        public GlobalSessionStore(ILogger<GlobalSessionStore> logger, string? basePath = null)
        {
            _logger = logger;
            _basePath = basePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "sessions");
        }

        /// <summary>列出所有 Session 元数据</summary>
        public async Task<IReadOnlyList<SessionMetadata>> ListAllAsync(
            string? partitionId = null,
            CancellationToken ct = default)
        {
            var result = new List<SessionMetadata>();

            if (!Directory.Exists(_basePath))
                return result;

            var partitions = partitionId != null
                ? new[] { partitionId }
                : Directory.GetDirectories(_basePath)
                    .Where(d => !Path.GetFileName(d).StartsWith("_"))
                    .ToArray();

            foreach (var partition in partitions)
            {
                var partitionName = Path.GetFileName(partition);
                foreach (var file in Directory.GetFiles(partition, "*.session.json"))
                {
                    try
                    {
                        var metadata = await ReadMetadataAsync(file, partitionName, ct);
                        if (metadata != null)
                            result.Add(metadata);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read session metadata: {File}", file);
                    }
                }
            }

            return result
                .OrderByDescending(m => m.LastActiveAt)
                .ToList();
        }

        /// <summary>统计信息</summary>
        public async Task<SessionStatistics> GetStatisticsAsync(CancellationToken ct = default)
        {
            var stats = new SessionStatistics();
            
            if (!Directory.Exists(_basePath))
                return stats;

            var partitions = Directory.GetDirectories(_basePath)
                .Where(d => !Path.GetFileName(d).StartsWith("_"))
                .ToArray();

            stats.PartitionCount = partitions.Length;

            foreach (var partition in partitions)
            {
                var files = Directory.GetFiles(partition, "*.session.json");
                stats.TotalSessions += files.Length;

                foreach (var file in files)
                {
                    try
                    {
                        var metadata = await ReadMetadataAsync(file, Path.GetFileName(partition), ct);
                        if (metadata != null)
                        {
                            stats.TotalMessages += metadata.MessageCount;
                            if (metadata.IsArchived) stats.ArchivedCount++;
                        }
                    }
                    catch { }
                }
            }

            // 归档统计
            var archivePath = Path.Combine(_basePath, "_archive");
            if (Directory.Exists(archivePath))
            {
                stats.ArchiveFileCount = Directory.GetFiles(archivePath, "*.json.gz").Length;
            }

            // 分享统计
            var sharePath = Path.Combine(_basePath, "_shares");
            if (Directory.Exists(sharePath))
            {
                stats.ShareCount = Directory.GetFiles(sharePath, "*.share.json").Length;
            }

            return stats;
        }

        private async Task<SessionMetadata?> ReadMetadataAsync(string filePath, string partitionId, CancellationToken ct)
        {
            using var stream = File.OpenRead(filePath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = doc.RootElement;

            return new SessionMetadata
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                PartitionId = partitionId,
                SelectedAgent = root.TryGetProperty("selectedAgent", out var agent) ? agent.GetString() : null,
                ParentSessionId = root.TryGetProperty("parentSessionId", out var parent) ? parent.GetString() : null,
                ForkLabel = root.TryGetProperty("forkLabel", out var label) ? label.GetString() : null,
                IsArchived = root.TryGetProperty("isArchived", out var archived) && archived.GetBoolean(),
                MessageCount = root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array
                    ? msgs.GetArrayLength()
                    : 0,
                CreatedAt = root.TryGetProperty("createdAt", out var created) && DateTime.TryParse(created.GetString(), out var cdt)
                    ? cdt
                    : DateTime.MinValue,
                LastActiveAt = root.TryGetProperty("lastActiveAt", out var active) && DateTime.TryParse(active.GetString(), out var adt)
                    ? adt
                    : DateTime.MinValue
            };
        }
    }

    /// <summary>Session 统计信息</summary>
    public class SessionStatistics
    {
        public int PartitionCount { get; set; }
        public int TotalSessions { get; set; }
        public int TotalMessages { get; set; }
        public int ArchivedCount { get; set; }
        public int ArchiveFileCount { get; set; }
        public int ShareCount { get; set; }
    }
}
