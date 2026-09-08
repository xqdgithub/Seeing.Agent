namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 快照 Diff 结果
    /// </summary>
    public class SnapshotDiff
    {
        /// <summary>快照 ID 1</summary>
        public string SnapshotId1 { get; init; } = "";

        /// <summary>快照 ID 2</summary>
        public string SnapshotId2 { get; init; } = "";

        /// <summary>新增行数</summary>
        public int AddedLines { get; init; }

        /// <summary>删除行数</summary>
        public int DeletedLines { get; init; }

        /// <summary>未变更行数</summary>
        public int UnchangedLines { get; init; }

        /// <summary>Unified Diff 格式输出</summary>
        public string UnifiedDiff { get; init; } = "";
    }
}
