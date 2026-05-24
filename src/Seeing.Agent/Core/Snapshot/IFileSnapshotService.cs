namespace Seeing.Agent.Core.Snapshot;

/// <summary>
/// 文件快照服务接口
/// </summary>
public interface IFileSnapshotService
{
    /// <summary>
    /// 为指定文件创建快照
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="operation">操作类型（可选）</param>
    /// <param name="sessionId">会话 ID（可选）</param>
    /// <returns>创建的快照</returns>
    Task<FileSnapshot> CreateAsync(string filePath, string? operation = null, string? sessionId = null);

    /// <summary>
    /// 获取指定 ID 的快照
    /// </summary>
    /// <param name="snapshotId">快照 ID</param>
    /// <returns>快照，不存在则返回 null</returns>
    Task<FileSnapshot?> GetAsync(string snapshotId);

    /// <summary>
    /// 恢复到指定快照
    /// </summary>
    /// <param name="snapshotId">快照 ID</param>
    /// <returns>恢复是否成功</returns>
    Task<bool> RestoreAsync(string snapshotId);

    /// <summary>
    /// 清除快照
    /// </summary>
    /// <param name="sessionId">会话 ID（可选，为 null 时清除所有）</param>
    Task ClearAsync(string? sessionId = null);
}