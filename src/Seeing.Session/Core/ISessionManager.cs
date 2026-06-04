namespace Seeing.Session.Core
{
    /// <summary>
    /// Session 管理器接口 - 管理会话的创建、存储和检索
    /// </summary>
    /// <remarks>
    /// 新架构设计：
    /// - 使用 SessionData 作为核心数据结构
    /// - 所有方法使用异步模式
    /// - 支持持久化和压缩扩展
    /// </remarks>
    public interface ISessionManager
    {
        /// <summary>创建新会话</summary>
        SessionData Create(string? partitionId = null, string? selectedAgent = null);

        /// <summary>获取会话</summary>
        SessionData? Get(string id);

        /// <summary>删除会话</summary>
        bool Delete(string id);

        /// <summary>注册现有会话到缓存</summary>
        void Register(SessionData session);

        /// <summary>列出所有会话</summary>
        IReadOnlyList<SessionData> List();

        /// <summary>保存会话到存储</summary>
        Task SaveAsync(string id);

        /// <summary>从存储加载会话</summary>
        Task<SessionData?> LoadAsync(string id);

        /// <summary>压缩会话消息</summary>
        IReadOnlyList<SessionMessage> Compress(string id);

        // === 新增方法 ===

        /// <summary>添加消息到会话</summary>
        Task AddMessageAsync(string sessionId, SessionMessage message, CancellationToken ct = default);

        /// <summary>Fork Session - 创建分支</summary>
        Task<SessionData> ForkAsync(
            string sessionId,
            string? atMessageId = null,
            string? label = null,
            CancellationToken ct = default);

        /// <summary>Archive Session - 归档</summary>
        Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default);

        /// <summary>Share Session - 分享</summary>
        Task<string> ShareAsync(string sessionId, CancellationToken ct = default);

        /// <summary>Revert Session - 回滚到指定消息</summary>
        Task<bool> RevertAsync(
            string sessionId, string messageId, CancellationToken ct = default);

        /// <summary>列出所有 Session (全局)</summary>
        Task<IReadOnlyList<SessionMetadata>> ListAllAsync(
            string? partitionId = null, CancellationToken ct = default);

        /// <summary>设置会话标题</summary>
        Task SetTitleAsync(string sessionId, string title, CancellationToken ct = default);
    }

    /// <summary>Session 元数据（用于列表显示）</summary>
    public class SessionMetadata
    {
        public string Id { get; init; } = string.Empty;
        public string? PartitionId { get; init; }
        public string? SelectedAgent { get; init; }
        public string? ParentSessionId { get; init; }
        public string? ForkLabel { get; init; }
        public bool IsArchived { get; init; }
        public int MessageCount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastActiveAt { get; init; }
    }
}