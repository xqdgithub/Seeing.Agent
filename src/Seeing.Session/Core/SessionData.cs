using System.Linq;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// 会话级自动批准策略
    /// </summary>
    public enum SessionAutoApprove
    {
        /// <summary>跟随全局配置（默认）</summary>
        FollowGlobal = 0,
        /// <summary>强制自动批准</summary>
        Enabled = 1,
        /// <summary>强制交互式确认</summary>
        Disabled = 2
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
        /// <summary>选中的 Agent ID（未设置时由 Seeing.Agent 解析 DefaultAgent）</summary>
        public string SelectedAgent { get; set; } = string.Empty;

        /// <summary>选中的模型引用（完整 modelRef，如 openai/gpt-4o 或 ACP 模型 ID）</summary>
        public string SelectedModel { get; set; } = string.Empty;

        /// <summary>ACP 透传 session mode（如 build / ask）</summary>
        public string SelectedAcpMode { get; set; } = string.Empty;

        // === 工作目录 ===
        public string? WorkingDirectory { get; set; }

        // === 状态 ===
        public SessionStatus Status { get; set; } = SessionStatus.Created;

        /// <summary>出站渠道 ID（如 qq / wecom）；空表示仅本地 Session</summary>
        public string? ChannelId { get; set; }

        /// <summary>出站用户/对端 ID；可空</summary>
        public string? UserId { get; set; }

        /// <summary>会话关系类型（Root / Fork / SubAgent）</summary>
        public SessionKind Kind { get; set; } = SessionKind.Root;

        /// <summary>子 Agent 会话权限快照（可序列化；续跑复用不重算）</summary>
        public List<SessionPermissionRule> PermissionSnapshot { get; set; } = new();

        /// <summary>会话级自动批准策略（默认跟随全局配置）</summary>
        public SessionAutoApprove AutoApprove { get; set; } = SessionAutoApprove.FollowGlobal;

        // === 消息历史 ===
        private List<SessionMessage> _messages = new();

        /// <summary>
        /// 消息列表（只读视图）。写入请使用 <see cref="AddMessage"/> 等统一编辑 API，
        /// 它们会为消息补写 <see cref="SessionMessage.SessionId"/> 归属。
        /// <para>保留 public setter：System.Text.Json 反序列化旧会话文件需要。</para>
        /// </summary>
        public IReadOnlyList<SessionMessage> Messages
        {
            get => _messages;
            set
            {
                _messages = value is List<SessionMessage> list
                    ? list
                    : value?.ToList() ?? new List<SessionMessage>();
            }
        }

        /// <summary>
        /// 获取活跃消息列表：应传递给 LLM 的统一消息来源。
        /// <para>以最后一个摘要消息（<see cref="SessionMessage.IsSummary"/>）为唯一压缩真相：
        /// 摘要及其之后均为活跃（压缩结果即真实状态），摘要之前均为已压缩历史。
        /// 无需额外标记——位置约束天然可靠，不受分支共享、旧数据缺失标记等影响。</para>
        /// <para>无摘要时（未压缩过或摘要被移除）返回全部消息。</para>
        /// <para>完整历史（含已压缩部分）仍通过 <see cref="Messages"/> 展示。</para>
        /// </summary>
        public List<SessionMessage> GetActiveMessages()
        {
            // 先做快照（内部 List 拷贝走 CopyTo 快速路径，无版本检查）：并发追加消息（事件管道）时不抛异常，
            // 与 TokenBudget 估算并发执行的场景一致
            var snapshot = new List<SessionMessage>(_messages);

            var lastSummaryIndex = -1;
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                if (snapshot[i].IsSummary)
                {
                    lastSummaryIndex = i;
                    break;
                }
            }

            if (lastSummaryIndex < 0)
            {
                return snapshot;
            }

            return snapshot.Skip(lastSummaryIndex).ToList();
        }

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

        // === Token 预算配置 ===
        /// <summary>
        /// Token 预算配置（覆盖 Agent 和全局配置）
        /// </summary>
        public TokenBudgetConfig? BudgetConfig { get; set; }

        /// <summary>
        /// 是否待压缩（下次请求前执行）
        /// </summary>
        [JsonIgnore]
        public bool PendingCompaction { get; set; }

        // === Token Usage 缓存（来自 LLM Provider） ===
        /// <summary>
        /// 缓存的总输入 Token 数（来自 LLM Provider Usage）
        /// </summary>
        public int? CachedInputTokens { get; set; }

        /// <summary>
        /// 缓存的总输出 Token 数（来自 LLM Provider Usage）
        /// </summary>
        public int? CachedOutputTokens { get; set; }

        /// <summary>
        /// 缓存更新时间
        /// </summary>
        public DateTime? CachedUsageUpdatedAt { get; set; }

        // === 向后兼容字段（Deprecated） ===

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
                SelectedAgent = selectedAgent ?? string.Empty,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now,
                Status = SessionStatus.Created
            };
        }

        // === 操作方法 ===
        public void AddMessage(SessionMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (string.IsNullOrEmpty(message.SessionId))
                message.SessionId = Id;
            _messages.Add(message);
            UpdatedAt = DateTime.Now;
            LastActiveAt = DateTime.Now;
        }

        /// <summary>
        /// 批量添加消息（写入时补写 <see cref="SessionMessage.SessionId"/>）。
        /// </summary>
        public void AddMessages(IEnumerable<SessionMessage> messages)
        {
            if (messages == null) throw new ArgumentNullException(nameof(messages));

            var added = false;
            foreach (var message in messages)
            {
                if (message == null) throw new ArgumentNullException(nameof(messages));
                message.SessionId ??= Id;
                _messages.Add(message);
                added = true;
            }

            if (!added)
                return;

            UpdatedAt = DateTime.Now;
            LastActiveAt = DateTime.Now;
        }

        /// <summary>
        /// 在指定位置插入消息（写入时补写 <see cref="SessionMessage.SessionId"/>）。
        /// </summary>
        public void InsertMessage(int index, SessionMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.SessionId ??= Id;
            _messages.Insert(index, message);
            UpdatedAt = DateTime.Now;
            LastActiveAt = DateTime.Now;
        }

        /// <summary>
        /// 按引用移除指定消息；返回是否移除。
        /// </summary>
        public bool RemoveMessage(SessionMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (!_messages.Remove(message))
                return false;

            UpdatedAt = DateTime.Now;
            return true;
        }

        /// <summary>
        /// 从尾部查找第一条匹配的消息并移除；返回是否移除。
        /// </summary>
        public bool RemoveLastMessage(Predicate<SessionMessage> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (!match(_messages[i]))
                    continue;

                _messages.RemoveAt(i);
                UpdatedAt = DateTime.Now;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 移除所有匹配的消息；返回移除数量。
        /// </summary>
        public int RemoveMessages(Predicate<SessionMessage> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            var removed = _messages.RemoveAll(match);
            if (removed > 0)
                UpdatedAt = DateTime.Now;

            return removed;
        }

        /// <summary>
        /// 清空并批量替换消息；全部消息改写 <see cref="SessionMessage.SessionId"/> 为当前会话 Id。
        /// <para>用于 Fork/分支/回滚等赋值场景：替换后消息强归属当前会话。</para>
        /// </summary>
        public void ReplaceMessages(IEnumerable<SessionMessage> messages)
        {
            if (messages == null) throw new ArgumentNullException(nameof(messages));

            // 先物化输入再清空：调用方可能传入基于本会话 Messages 的惰性序列
            // （如回滚场景 session.Messages.Take(n)），若先 Clear 再枚举会清空全部消息。
            var materialized = messages.ToList();
            _messages.Clear();
            foreach (var message in materialized)
            {
                if (message == null) throw new ArgumentNullException(nameof(messages));
                message.SessionId = Id;
                _messages.Add(message);
            }

            UpdatedAt = DateTime.Now;
            LastActiveAt = DateTime.Now;
        }

        public void SetAgentConfig(string agent, string? model = null)
        {
            SelectedAgent = agent;
            if (model != null) SelectedModel = model;
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
            _messages.Clear();
            Metadata.Remove(SessionMetadataKeys.InstructionFingerprints);
            // 清空会话应同时去掉运行时上下文（如 todos），否则下次首条消息会因未完成 todo 再续一轮
            Context.Clear();
            CachedInputTokens = null;
            CachedOutputTokens = null;
            CachedUsageUpdatedAt = null;
            UpdatedAt = DateTime.Now;
        }

        /// <summary>
        /// 清除 Token Usage 缓存
        /// </summary>
        public void ClearUsageCache()
        {
            CachedInputTokens = null;
            CachedOutputTokens = null;
            CachedUsageUpdatedAt = null;
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
                SelectedAcpMode = SelectedAcpMode,
                WorkingDirectory = WorkingDirectory,
                Status = Status,
                ChannelId = ChannelId,
                UserId = UserId,
                Kind = Kind,
                AutoApprove = AutoApprove,
                PermissionSnapshot = PermissionSnapshot.Select(r => new SessionPermissionRule
                {
                    Kind = r.Kind,
                    Pattern = r.Pattern,
                    Effect = r.Effect,
                    Priority = r.Priority
                }).ToList(),
                _messages = Messages.Select(m => m.Clone()).ToList(),
                Context = new Dictionary<string, object>(Context),
                Metadata = new Dictionary<string, string>(Metadata),
                State = new Dictionary<string, string>(State),
                // 新增字段
                ParentSessionId = ParentSessionId,
                ForkLabel = ForkLabel,
                IsArchived = IsArchived,
                ArchivedAt = ArchivedAt,
                // Token 预算配置
                BudgetConfig = BudgetConfig,
                // Token Usage 缓存
                CachedInputTokens = CachedInputTokens,
                CachedOutputTokens = CachedOutputTokens,
                CachedUsageUpdatedAt = CachedUsageUpdatedAt
            };
        }
    }
}
