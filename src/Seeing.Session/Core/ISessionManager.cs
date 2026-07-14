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

        /// <summary>确保会话存在（缓存 → 存储 → 创建）</summary>
        Task<SessionData> EnsureSessionAsync(
            string id,
            string? selectedAgent = null,
            string? partitionId = null);

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

        /// <summary>
        /// 从存储加载所有会话并注册到缓存
        /// <para>用于启动时恢复会话列表。</para>
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话列表（已按更新时间降序排列）</returns>
        Task<IReadOnlyList<SessionData>> LoadAllFromStorageAsync(CancellationToken ct = default);

        /// <summary>设置会话标题</summary>
        Task SetTitleAsync(string sessionId, string title, CancellationToken ct = default);

        /// <summary>
        /// 设置会话的模型
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="modelId">模型 ID（可以是 "model" 或 "provider/model" 格式）</param>
        /// <param name="providerId">Provider ID（可选，可从 modelId 解析）</param>
        /// <param name="ct">取消令牌</param>
        Task SetModelAsync(string sessionId, string modelId, string? providerId = null, CancellationToken ct = default);

        // === 原子操作方法（确保缓存一致性） ===

        /// <summary>
        /// 获取或加载会话（原子操作）
        /// <para>优先从内存缓存获取，若不存在则从存储加载。</para>
        /// <para>这是获取 Session 的推荐方法，确保所有组件使用同一实例。</para>
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>SessionData 实例</returns>
        /// <exception cref="InvalidOperationException">会话不存在</exception>
        Task<SessionData> GetOrLoadAsync(string sessionId, CancellationToken ct = default);

        /// <summary>
        /// 原子更新会话（确保缓存一致性）
        /// <para>从缓存获取 Session，执行更新操作，然后持久化。</para>
        /// <para>此方法确保更新操作在正确的实例上执行，并自动保存。</para>
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="updateAction">更新操作</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>更新后的 SessionData</returns>
        /// <exception cref="InvalidOperationException">会话不存在</exception>
        Task<SessionData> UpdateSessionAsync(string sessionId, Action<SessionData> updateAction, CancellationToken ct = default);

        /// <summary>
        /// 保存并通知更新（可选持久化）
        /// <para>更新会话的 UpdatedAt 时间戳，触发 Updated Hook，并可选保存到存储。</para>
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="persist">是否持久化到存储</param>
        /// <param name="ct">取消令牌</param>
        Task SaveAndNotifyAsync(string sessionId, bool persist = true, CancellationToken ct = default);
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