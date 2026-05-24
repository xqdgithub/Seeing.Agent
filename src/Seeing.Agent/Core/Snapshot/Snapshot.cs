namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 文件快照实体
    /// </summary>
    public class Snapshot
    {
        /// <summary>快照 ID</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>文件路径</summary>
        public string FilePath { get; init; } = "";

        /// <summary>会话 ID</summary>
        public string SessionId { get; init; } = "";

        /// <summary>标签</summary>
        public string? Label { get; init; }

        /// <summary>内容哈希 (SHA256)</summary>
        public string ContentHash { get; init; } = "";

        /// <summary>创建时间</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>文件大小</summary>
        public long FileSize { get; init; }

        /// <summary>基础快照 ID（Diff 模式）</summary>
        public string? BaseSnapshotId { get; init; }

        /// <summary>Diff 补丁（序列化）</summary>
        public string? DiffPatches { get; init; }

        /// <summary>是否为完整内容快照</summary>
        public bool IsFullSnapshot => string.IsNullOrEmpty(BaseSnapshotId);
    }
}
