namespace Seeing.Agent.Core.Background
{
    /// <summary>
    /// 后台任务进度模型
    /// </summary>
    public class BackgroundTaskProgress
    {
        /// <summary>任务 ID</summary>
        public string TaskId { get; init; } = string.Empty;

        /// <summary>进度百分比 (0-100)</summary>
        public int Percent { get; init; }

        /// <summary>进度消息</summary>
        public string? Message { get; init; }

        /// <summary>时间戳</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>进度类型</summary>
        public ProgressType Type { get; init; } = ProgressType.Update;
    }

    /// <summary>进度类型</summary>
    public enum ProgressType
    {
        Update,
        Output,
        Error,
        Completed
    }
}