using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using Seeing.Session.Storage;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 归档器
    /// </summary>
    public class SessionArchiver
    {
        private readonly ILogger<SessionArchiver> _logger;
        private readonly string _archivePath;

        public SessionArchiver(ILogger<SessionArchiver> logger, string? archivePath = null)
        {
            _logger = logger;
            _archivePath = archivePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "sessions", "_archive");
        }

        /// <summary>Archive Session - 归档</summary>
        public async Task<bool> ArchiveAsync(SessionData session, CancellationToken ct = default)
        {
            try
            {
                // 标记为归档
                session.IsArchived = true;
                session.ArchivedAt = DateTimeOffset.UtcNow;
                session.Status = SessionStatus.Archived;

                // 序列化
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                // 压缩并保存
                Directory.CreateDirectory(_archivePath);
                var fileName = $"{session.Id}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json.gz";
                var filePath = Path.Combine(_archivePath, fileName);

                using var fileStream = File.Create(filePath);
                using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                using var writer = new StreamWriter(gzipStream);
                await writer.WriteAsync(json);

                _logger.LogInformation("Archived session {SessionId} to {FilePath}", session.Id, filePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to archive session {SessionId}", session.Id);
                return false;
            }
        }

        /// <summary>列出归档的 Session</summary>
        public IReadOnlyList<ArchiveInfo> ListArchives()
        {
            var result = new List<ArchiveInfo>();
            if (!Directory.Exists(_archivePath)) return result;

            foreach (var file in Directory.GetFiles(_archivePath, "*.json.gz"))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
                    var parts = fileName.Split('_');
                    if (parts.Length >= 2)
                    {
                        result.Add(new ArchiveInfo
                        {
                            SessionId = parts[0],
                            ArchivedAt = DateTimeOffset.TryParse(parts[1], out var dt) ? dt : DateTimeOffset.MinValue,
                            FilePath = file,
                            SizeBytes = new FileInfo(file).Length
                        });
                    }
                }
                catch { /* skip invalid files */ }
            }

            return result;
        }

        /// <summary>恢复归档的 Session</summary>
        public async Task<SessionData?> RestoreAsync(string archivePath, CancellationToken ct = default)
        {
            try
            {
                using var fileStream = File.OpenRead(archivePath);
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream);
                var json = await reader.ReadToEndAsync(ct);

                var session = JsonSerializer.Deserialize<SessionData>(json);
                if (session != null)
                {
                    session.IsArchived = false;
                    session.ArchivedAt = null;
                    session.Status = SessionStatus.Active;
                }
                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore archive: {ArchivePath}", archivePath);
                return null;
            }
        }
    }

    /// <summary>归档信息</summary>
    public class ArchiveInfo
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset ArchivedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }
}
