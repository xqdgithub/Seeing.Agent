namespace Seeing.Session.Core
{
    public enum SessionStatus
    {
        /// <summary>Created (已创建)</summary>
        Created = 0,
        /// <summary>Active (活跃)</summary>
        Active = 1,
        /// <summary>Idle (空闲)</summary>
        Idle = 2,
        /// <summary>Completed (已完成)</summary>
        Completed = 3,
        /// <summary>Archived (已归档)</summary>
        Archived = 4,
        /// <summary>Error (错误状态)</summary>
        Error = 5
    }

    public class SessionData
    {
        // === 身份信息 ===
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string PartitionId { get; set; } = string.Empty;

        // === 时间信息 ===
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime LastActiveAt { get; set; }

        // === Agent 配置（一级字段） ===
        /// <summary>选中的 Agent ID（如 primary, build 等）</summary>
        public string SelectedAgent { get; set; } = "primary";

        /// <summary>选中的 Model ID（如 gpt-4o, claude-3-5-sonnet 等）</summary>
        public string SelectedModel { get; set; } = string.Empty;

        /// <summary>Model 所属 Provider ID（如 openai, anthropic 等）</summary>
        public string SelectedModelProvider { get; set; } = string.Empty;

        // === 工作目录 ===
        public string? WorkingDirectory { get; set; }

        // === 状态 ===
        public SessionStatus Status { get; set; } = SessionStatus.Created;

        // === 消息历史 ===
        public List<SessionMessage> Messages { get; set; } = new();

        // === 扩展上下文（用于存储其他运行时数据） ===
        public Dictionary<string, object> Context { get; set; } = new();

        // === 元数据（用于存储用户自定义标签等） ===
        public Dictionary<string, string> Metadata { get; set; } = new();

        // === Fork/Archive 支持（新增） ===
        /// <summary>父会话 ID（Fork 时设置）</summary>
        public string? ParentSessionId { get; set; }

        /// <summary>Fork 标签</summary>
        public string? ForkLabel { get; set; }

        /// <summary>是否已归档</summary>
        public bool IsArchived { get; set; }

        /// <summary>归档时间</summary>
        public DateTimeOffset? ArchivedAt { get; set; }

        // === 向后兼容字段（Deprecated） ===
        [Obsolete("使用 SelectedAgent 替代")]
        public AgentMetadata? Agent { get; set; }

        public Dictionary<string, string> State { get; set; } = new Dictionary<string, string>();

        // === 统计属性 ===
        public int MessageCount => Messages.Count;

        // === 工厂方法 ===
        public static SessionData Create(string? partitionId = null, string? selectedAgent = null)
        {
            var id = $"ses_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            return new SessionData
            {
                Id = id,
                Title = $"Session {id}",
                PartitionId = partitionId ?? "default",
                SelectedAgent = selectedAgent ?? "primary",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
                Status = SessionStatus.Created
            };
        }

        // === 操作方法 ===
        public void AddMessage(SessionMessage message)
        {
            Messages.Add(message ?? throw new ArgumentNullException(nameof(message)));
            UpdatedAt = DateTime.Now;
            LastActiveAt = DateTime.Now;
        }

        public void SetAgentConfig(string agent, string? model = null, string? provider = null)
        {
            SelectedAgent = agent;
            if (model != null) SelectedModel = model;
            if (provider != null) SelectedModelProvider = provider;
            UpdatedAt = DateTime.Now;
        }

        public void SetContext(string key, object value)
        {
            Context[key] = value;
            UpdatedAt = DateTime.Now;
        }

        public T? GetContext<T>(string key)
        {
            if (Context.TryGetValue(key, out var value) && value is T typed)
                return typed;
            return default;
        }

        public bool TryGetContext<T>(string key, out T? value)
        {
            if (Context.TryGetValue(key, out var obj) && obj is T typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }

        public void ClearMessages()
        {
            Messages.Clear();
            UpdatedAt = DateTime.Now;
        }

        public void RemoveContext(string key)
        {
            Context.Remove(key);
            UpdatedAt = DateTime.Now;
        }

        // === 深拷贝 ===
        public SessionData Clone()
        {
            return new SessionData
            {
                Id = Id,
                Title = Title,
                PartitionId = PartitionId,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                LastActiveAt = LastActiveAt,
                SelectedAgent = SelectedAgent,
                SelectedModel = SelectedModel,
                SelectedModelProvider = SelectedModelProvider,
                WorkingDirectory = WorkingDirectory,
                Status = Status,
                Messages = new List<SessionMessage>(Messages),
                Context = new Dictionary<string, object>(Context),
                Metadata = new Dictionary<string, string>(Metadata),
                State = new Dictionary<string, string>(State),
                // 新增字段
                ParentSessionId = ParentSessionId,
                ForkLabel = ForkLabel,
                IsArchived = IsArchived,
                ArchivedAt = ArchivedAt
            };
        }
    }
}
