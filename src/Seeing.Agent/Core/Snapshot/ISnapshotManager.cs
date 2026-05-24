namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 快照管理器接口
    /// </summary>
    public interface ISnapshotManager
    {
        /// <summary>创建文件快照</summary>
        Task<Snapshot> CreateSnapshotAsync(
            string filePath,
            string sessionId,
            string? label = null,
            CancellationToken cancellationToken = default);

        /// <summary>获取文件的所有快照</summary>
        Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(
            string filePath,
            string? sessionId = null,
            CancellationToken cancellationToken = default);

        /// <summary>计算两个快照之间的 Diff</summary>
        Task<SnapshotDiff> ComputeDiffAsync(
            string snapshotId1,
            string snapshotId2,
            CancellationToken cancellationToken = default);

        /// <summary>计算快照与当前文件的 Diff</summary>
        Task<SnapshotDiff> ComputeDiffWithCurrentAsync(
            string snapshotId,
            CancellationToken cancellationToken = default);

        /// <summary>恢复到指定快照</summary>
        Task<bool> RestoreAsync(
            string snapshotId,
            CancellationToken cancellationToken = default);

        /// <summary>删除快照</summary>
        Task<bool> DeleteSnapshotAsync(
            string snapshotId,
            CancellationToken cancellationToken = default);

        /// <summary>清理过期快照</summary>
        Task<int> CleanupAsync(
            TimeSpan maxAge,
            CancellationToken cancellationToken = default);

        /// <summary>获取快照的完整内容（自动应用 Diff 链）</summary>
        Task<string> GetSnapshotContentAsync(
            string snapshotId,
            CancellationToken cancellationToken = default);
    }
}
