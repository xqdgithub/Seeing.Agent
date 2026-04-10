using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Snapshot;

/// <summary>
/// 文件快照服务实现
/// </summary>
public class FileSnapshotService : IFileSnapshotService
{
    private readonly ILogger<FileSnapshotService> _logger;
    private readonly ConcurrentDictionary<string, FileSnapshot> _snapshots = new();

    /// <summary>
    /// 创建文件快照服务
    /// </summary>
    public FileSnapshotService(ILogger<FileSnapshotService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 为指定文件创建快照
    /// </summary>
    public async Task<FileSnapshot> CreateAsync(string filePath, string? operation = null, string? sessionId = null)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"文件不存在: {filePath}", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath);
        var snapshot = new FileSnapshot
        {
            FilePath = filePath,
            Content = content,
            Operation = operation,
            SessionId = sessionId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _snapshots[snapshot.Id] = snapshot;
        _logger.LogInformation("创建文件快照: {SnapshotId}, 文件: {FilePath}, 操作: {Operation}", 
            snapshot.Id, filePath, operation ?? "无");

        return snapshot;
    }

    /// <summary>
    /// 获取指定 ID 的快照
    /// </summary>
    public Task<FileSnapshot?> GetAsync(string snapshotId)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// 恢复到指定快照
    /// </summary>
    public async Task<bool> RestoreAsync(string snapshotId)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
        {
            _logger.LogWarning("快照不存在: {SnapshotId}", snapshotId);
            return false;
        }

        try
        {
            // 确保目标目录存在
            var directory = Path.GetDirectoryName(snapshot.FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(snapshot.FilePath, snapshot.Content);
            _logger.LogInformation("恢复文件快照: {SnapshotId}, 文件: {FilePath}", snapshotId, snapshot.FilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复文件快照失败: {SnapshotId}", snapshotId);
            return false;
        }
    }

    /// <summary>
    /// 清除快照
    /// </summary>
    public Task ClearAsync(string? sessionId = null)
    {
        if (sessionId is null)
        {
            // 清除所有快照
            var count = _snapshots.Count;
            _snapshots.Clear();
            _logger.LogInformation("清除所有快照: {Count}", count);
        }
        else
        {
            // 清除指定会话的快照
            var keysToRemove = _snapshots
                .Where(kvp => kvp.Value.SessionId == sessionId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _snapshots.TryRemove(key, out _);
            }

            _logger.LogInformation("清除会话快照: {SessionId}, 数量: {Count}", sessionId, keysToRemove.Count);
        }

        return Task.CompletedTask;
    }
}