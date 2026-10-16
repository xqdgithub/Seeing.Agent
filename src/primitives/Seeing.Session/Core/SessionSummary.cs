namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话摘要：会话列表 / 归档列表展示所需的元数据（不含消息体）。
    /// </summary>
    /// <remarks>
    /// 轻量只读 DTO：由 <c>ISessionSummaryProvider</c> 或
    /// <c>SessionStoreExtensions.ListSummariesAsync</c> 降级投影产出。
    /// </remarks>
    public sealed class SessionSummary
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? PartitionId { get; init; }
        public string? SelectedAgent { get; init; }
        public string? SelectedModel { get; init; }
        public SessionStatus Status { get; init; }
        public SessionKind Kind { get; init; }
        public string? GroupId { get; init; }
        public bool IsArchived { get; init; }
        public int MessageCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime LastActiveAt { get; init; }
        public string? ChannelId { get; init; }
        public string? UserId { get; init; }
        public string? WorkingDirectory { get; init; }
        public string? Scenario { get; init; }
        public SessionAutoApprove AutoApprove { get; init; }

        /// <summary>从完整会话投影摘要（降级路径 / 内存后端使用）。</summary>
        public static SessionSummary FromSession(SessionData s) => new()
        {
            Id = s.Id,
            Title = s.Title,
            PartitionId = s.PartitionId,
            SelectedAgent = s.SelectedAgent,
            SelectedModel = s.SelectedModel,
            Status = s.Status,
            Kind = s.Kind,
            GroupId = s.GroupId,
            IsArchived = s.IsArchived,
            MessageCount = s.MessageCount,
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            LastActiveAt = s.LastActiveAt,
            ChannelId = s.ChannelRoute?.ChannelId,
            WorkingDirectory = s.WorkingDirectory,
            Scenario = s.Scenario,
            AutoApprove = s.AutoApprove
        };
    }
}
