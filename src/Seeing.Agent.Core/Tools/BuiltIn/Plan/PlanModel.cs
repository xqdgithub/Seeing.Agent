namespace Seeing.Agent.Tools.BuiltIn.Plan
{
    /// <summary>
    /// 计划模型
    /// </summary>
    public class PlanModel
    {
        /// <summary>计划 ID</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>计划名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>计划描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>任务列表</summary>
        public List<PlanTask> Tasks { get; set; } = new();

        /// <summary>计划状态</summary>
        public PlanStatus Status { get; set; } = PlanStatus.Draft;

        /// <summary>创建时间</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>更新时间</summary>
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>会话 ID</summary>
        public string? SessionId { get; set; }

        /// <summary>关联文件路径</summary>
        public string? FilePath { get; set; }

        /// <summary>元数据</summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>计划任务</summary>
    public class PlanTask
    {
        /// <summary>任务 ID</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>任务标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>任务描述</summary>
        public string? Description { get; set; }

        /// <summary>任务状态</summary>
        public PlanTaskStatus Status { get; set; } = PlanTaskStatus.Pending;

        /// <summary>优先级</summary>
        public int Priority { get; set; }

        /// <summary>依赖任务 ID 列表</summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>分配的 Agent</summary>
        public string? AssignedAgent { get; set; }

        /// <summary>预估时间（分钟）</summary>
        public int? EstimatedMinutes { get; set; }

        /// <summary>实际时间（分钟）</summary>
        public int? ActualMinutes { get; set; }

        /// <summary>完成时间</summary>
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>结果</summary>
        public string? Result { get; set; }
    }

    /// <summary>计划状态</summary>
    public enum PlanStatus
    {
        Draft,
        Active,
        InProgress,
        Completed,
        Cancelled
    }

    /// <summary>任务状态</summary>
    public enum PlanTaskStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Skipped
    }
}
