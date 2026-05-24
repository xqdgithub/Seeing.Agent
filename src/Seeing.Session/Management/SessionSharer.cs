using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 分享器
    /// </summary>
    public class SessionSharer
    {
        private readonly ILogger<SessionSharer> _logger;
        private readonly string _sharePath;
        private readonly ConcurrentDictionary<string, ShareRecord> _shares = new();

        public SessionSharer(ILogger<SessionSharer> logger, string? sharePath = null)
        {
            _logger = logger;
            _sharePath = sharePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "sessions", "_shares");
        }

        /// <summary>Share Session - 分享</summary>
        public async Task<string> ShareAsync(SessionData session, CancellationToken ct = default)
        {
            var shareId = Guid.NewGuid().ToString("N");
            var shareRecord = new ShareRecord
            {
                ShareId = shareId,
                SessionId = session.Id,
                SessionData = session.Clone(),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            };

            _shares[shareId] = shareRecord;

            // 持久化
            Directory.CreateDirectory(_sharePath);
            var filePath = Path.Combine(_sharePath, $"{shareId}.share.json");
            var json = JsonSerializer.Serialize(shareRecord, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, ct);

            _logger.LogInformation("Shared session {SessionId} as {ShareId}", session.Id, shareId);

            return $"session://share/{shareId}";
        }

        /// <summary>解析分享</summary>
        public async Task<SessionData?> ResolveAsync(string shareId, CancellationToken ct = default)
        {
            // 先从内存查找
            if (_shares.TryGetValue(shareId, out var record))
            {
                if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTimeOffset.UtcNow)
                {
                    _shares.TryRemove(shareId, out _);
                    _logger.LogWarning("Share {ShareId} has expired", shareId);
                    return null;
                }
                return record.SessionData?.Clone();
            }

            // 从文件加载
            var filePath = Path.Combine(_sharePath, $"{shareId}.share.json");
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath, ct);
                record = JsonSerializer.Deserialize<ShareRecord>(json);
                if (record == null) return null;

                // 检查过期
                if (record.ExpiresAt.HasValue && record.ExpiresAt.Value < DateTimeOffset.UtcNow)
                {
                    File.Delete(filePath);
                    _logger.LogWarning("Share {ShareId} has expired", shareId);
                    return null;
                }

                _shares[shareId] = record;
                return record.SessionData?.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve share {ShareId}", shareId);
                return null;
            }
        }

        /// <summary>撤销分享</summary>
        public bool Revoke(string shareId)
        {
            _shares.TryRemove(shareId, out _);
            var filePath = Path.Combine(_sharePath, $"{shareId}.share.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            _logger.LogInformation("Revoked share {ShareId}", shareId);
            return true;
        }
    }

    /// <summary>分享记录</summary>
    public class ShareRecord
    {
        public string ShareId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public SessionData? SessionData { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
