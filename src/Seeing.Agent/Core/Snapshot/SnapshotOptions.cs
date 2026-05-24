namespace Seeing.Agent.Core.Snapshot
{
    /// <summary>
    /// 快照配置选项
    /// </summary>
    public class SnapshotOptions
    {
        /// <summary>存储路径</summary>
        public string StoragePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "snapshots");

        /// <summary>每个文件最大快照数</summary>
        public int MaxSnapshotsPerFile { get; set; } = 50;

        /// <summary>最大保留时间</summary>
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);
    }
}
