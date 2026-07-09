using Microsoft.Extensions.Logging;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Storage;
using System.Collections.Concurrent;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 管理器 - 管理会话的创建、存储和检索
    /// </summary>
    /// <remarks>
    /// 新架构设计：
    /// - 使用 SessionData + 可选组件注入 + SaveAsync/LoadAsync/Compress
    /// - 使用 IHookManager 触发生命周期钩子
    /// - 使用 ISessionEventPublisher 发布事件（供 UI 订阅）
    /// - 保持内存管理模式（ConcurrentDictionary）
    /// </remarks>
    public class SessionManager : ISessionManager
    {
        private readonly ISessionStore? _store;
        private readonly ICompressionStrategy? _compressor;
        private readonly IHookManager? _hookManager;
        private readonly ISessionEventPublisher? _eventPublisher;
        private readonly ILogger<SessionManager>? _logger;
        private readonly ConcurrentDictionary<string, SessionData> _sessionDataCache = new();

        // 新增组件（可选）
        private readonly SessionForker? _forker;
        private readonly SessionArchiver? _archiver;
        private readonly SessionSharer? _sharer;
        private readonly SessionReverter? _reverter;
        private readonly GlobalSessionStore? _globalStore;

        /// <summary>
        /// 创建 SessionManager 实例
        /// </summary>
        /// <param name="store">会话存储（可选）</param>
        /// <param name="compressor">压缩策略（可选，默认 SlidingWindowCompression）</param>
        /// <param name="hookManager">Hook 管理器（可选）</param>
        /// <param name="eventPublisher">事件发布器（可选，用于 UI 更新）</param>
        /// <param name="logger">日志（可选）</param>
        /// <param name="forker">Session 分支器（可选）</param>
        /// <param name="archiver">Session 归档器（可选）</param>
        /// <param name="sharer">Session 分享器（可选）</param>
        /// <param name="reverter">Session 回滚器（可选）</param>
        /// <param name="globalStore">全局 Session 存储（可选）</param>
        public SessionManager(
            ISessionStore? store = null,
            ICompressionStrategy? compressor = null,
            IHookManager? hookManager = null,
            ISessionEventPublisher? eventPublisher = null,
            ILogger<SessionManager>? logger = null,
            SessionForker? forker = null,
            SessionArchiver? archiver = null,
            SessionSharer? sharer = null,
            SessionReverter? reverter = null,
            GlobalSessionStore? globalStore = null)
        {
            _store = store;
            _compressor = compressor ?? new SlidingWindowCompression();
            _hookManager = hookManager;
            _eventPublisher = eventPublisher;
            _logger = logger;
            _forker = forker;
            _archiver = archiver;
            _sharer = sharer;
            _reverter = reverter;
            _globalStore = globalStore;
        }

        /// <summary>
        /// 创建新会话
        /// </summary>
        /// <param name="partitionId">分区 ID（可选）</param>
        /// <param name="selectedAgent">选中的 Agent ID（可选）</param>
        /// <returns>新创建的 SessionData</returns>
        public SessionData Create(string? partitionId = null, string? selectedAgent = null)
        {
            var session = SessionData.Create(partitionId, selectedAgent);
            _sessionDataCache[session.Id] = session;

            // 触发 Created Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Created,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session });

            _logger?.LogInformation("创建会话: {SessionId}, Partition: {PartitionId}, Agent: {Agent}",
                session.Id, partitionId ?? "default", selectedAgent ?? "(default)");
            return session;
        }

        /// <summary>
        /// 确保会话存在：先查缓存，再尝试从存储加载，均不存在则使用指定 ID 创建
        /// </summary>
        /// <param name="id">会话 ID（由调用方指定）</param>
        /// <param name="selectedAgent">选中的 Agent ID（可选，仅新建时生效）</param>
        /// <param name="partitionId">分区 ID（可选，仅新建时生效）</param>
        /// <returns>已存在或新创建的 SessionData</returns>
        public async Task<SessionData> EnsureSessionAsync(
            string id,
            string? selectedAgent = null,
            string? partitionId = null)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Session id cannot be null or empty.", nameof(id));

            var existing = Get(id);
            if (existing != null)
                return existing;

            var loaded = await LoadAsync(id);
            if (loaded != null)
                return loaded;

            var session = new SessionData
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
            _sessionDataCache[session.Id] = session;

            // 触发 Hook（内部事件）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Created,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session });

            // 发布 SessionEvent（通知 SessionProvider 等 UI 组件）
            _eventPublisher?.Publish(new SessionEvent
            {
                SessionId = session.Id,
                Type = SessionEventType.Created,
                Data = session
            });

            _logger?.LogInformation("创建会话: {SessionId}, Partition: {PartitionId}, Agent: {Agent}",
                session.Id, session.PartitionId, session.SelectedAgent);
            return session;
        }

        /// <summary>
        /// 获取会话
        /// </summary>
        /// <param name="id">会话 ID</param>
        /// <returns>SessionData 或 null</returns>
        public SessionData? Get(string id) =>
            string.IsNullOrEmpty(id) ? null : _sessionDataCache.TryGetValue(id, out var s) ? s : null;

        /// <summary>
        /// 删除会话
        /// </summary>
        /// <param name="id">会话 ID</param>
        /// <returns>是否成功删除</returns>
        public bool Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            if (_sessionDataCache.TryRemove(id, out var session))
            {
                // 触发 Destroyed Hook（非阻塞）
                _hookManager?.TriggerFireAndForget(
                    HookPoints.Destroyed,
                    session.Id,
                    result: new Dictionary<string, object?> { ["session"] = session });

                // 异步删除存储（fire-and-forget）
                if (_store != null)
                {
                    _ = _store.DeleteAsync(id).ConfigureAwait(false);
                }

                _logger?.LogInformation("删除会话: {SessionId}", id);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 注册现有会话到缓存
        /// </summary>
        /// <param name="session">要注册的会话</param>
        public void Register(SessionData session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            _sessionDataCache[session.Id] = session;

            // 触发 Created Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Created,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session });

            _logger?.LogInformation("注册会话: {SessionId}", session.Id);
        }

        /// <summary>
        /// 列出所有会话
        /// </summary>
        /// <returns>会话列表</returns>
        public IReadOnlyList<SessionData> List() => _sessionDataCache.Values.ToList();

        /// <summary>
        /// 保存会话到存储
        /// </summary>
        /// <param name="id">会话 ID</param>
        public async Task SaveAsync(string id)
        {
            var session = Get(id);
            if (session == null)
            {
                _logger?.LogWarning("会话不存在，无法保存: {SessionId}", id);
                return;
            }

            if (_store == null)
            {
                _logger?.LogWarning("未配置 SessionStore，无法保存: {SessionId}", id);
                return;
            }

            // 触发 Saving Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Saving,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session });

            // 保存克隆副本（避免后续修改影响）
            await _store.SaveAsync(session.Clone());

            // 触发 Saved Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Saved,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session });

            _logger?.LogInformation("保存会话: {SessionId}", id);
        }

        /// <summary>
        /// 从存储加载会话
        /// </summary>
        /// <param name="id">会话 ID</param>
        /// <returns>加载的 SessionData 或 null</returns>
        public async Task<SessionData?> LoadAsync(string id)
        {
            if (_store == null)
            {
                _logger?.LogWarning("未配置 SessionStore，无法加载: {SessionId}", id);
                return null;
            }

            // 触发 Loading Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Loading,
                id,
                input: new Dictionary<string, object?> { ["sessionId"] = id });

            var data = await _store.LoadAsync(id);
            if (data == null)
            {
                _logger?.LogWarning("会话不存在于存储: {SessionId}", id);
                return null;
            }

            // 缓存到内存
            _sessionDataCache[data.Id] = data;

            // 触发 Loaded Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Loaded,
                data.Id,
                result: new Dictionary<string, object?> { ["session"] = data });

            _logger?.LogInformation("加载会话: {SessionId}", id);
            return data;
        }

        /// <summary>
        /// 压缩会话消息
        /// </summary>
        /// <param name="id">会话 ID</param>
        /// <returns>压缩后保留的消息列表</returns>
        public IReadOnlyList<SessionMessage> Compress(string id)
        {
            var session = Get(id);
            if (session == null)
            {
                _logger?.LogWarning("会话不存在，无法压缩: {SessionId}", id);
                return Array.Empty<SessionMessage>();
            }

            if (_compressor == null)
            {
                _logger?.LogWarning("未配置 CompressionStrategy，无法压缩: {SessionId}", id);
                return session.Messages;
            }

            var original = session.Messages;
            if (original.Count == 0)
            {
                return original;
            }

            var compressed = _compressor.Compress(original);

            // 更新会话消息
            session.Messages.Clear();
            session.Messages.AddRange(compressed);

            // 触发 Compressed Hook（非阻塞）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Compressed,
                session.Id,
                result: new Dictionary<string, object?>
                {
                    ["session"] = session,
                    ["originalCount"] = original.Count,
                    ["compressedCount"] = compressed.Count
                });

            _logger?.LogInformation(
                "压缩会话消息: {SessionId}, 原始: {OriginalCount}, 保留: {CompressedCount}",
                id, original.Count, compressed.Count);

            return compressed;
        }

        // === 新增接口方法实现 ===

        /// <summary>
        /// 添加消息到会话
        /// </summary>
        public async Task AddMessageAsync(string sessionId, SessionMessage message, CancellationToken ct = default)
        {
            var session = Get(sessionId);
            if (session == null)
            {
                _logger?.LogWarning("会话不存在，无法添加消息: {SessionId}", sessionId);
                return;
            }

            session.AddMessage(message);

            // 触发 Updated Hook（TitleGenerationService 通过此 Hook 监听并生成标题）
            _hookManager?.TriggerFireAndForget(
                HookPoints.Updated,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session, ["message"] = message });

            // 发布 SessionEvent（通知 SessionProvider 等 UI 组件）
            _eventPublisher?.Publish(new SessionEvent
            {
                SessionId = session.Id,
                Type = SessionEventType.Updated,
                Data = session
            });

            // 自动保存
            if (_store != null)
            {
                await SaveAsync(sessionId);
            }
        }

        /// <summary>
        /// Fork Session - 创建分支
        /// </summary>
        public async Task<SessionData> ForkAsync(
            string sessionId,
            string? atMessageId = null,
            string? label = null,
            CancellationToken ct = default)
        {
            if (_forker == null)
            {
                throw new InvalidOperationException("SessionForker not configured. Inject SessionForker via constructor.");
            }

            var forkedSession = await _forker.ForkAsync(sessionId, atMessageId, label, ct);

            // 触发 Hook
            _hookManager?.TriggerFireAndForget(
                "session.forked",
                forkedSession.Id,
                result: new Dictionary<string, object?>
                {
                    ["session"] = forkedSession,
                    ["parentSessionId"] = sessionId
                });

            return forkedSession;
        }

        /// <summary>
        /// Archive Session - 归档
        /// </summary>
        public async Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default)
        {
            var session = Get(sessionId);
            if (session == null)
            {
                _logger?.LogWarning("会话不存在，无法归档: {SessionId}", sessionId);
                return false;
            }

            if (_archiver == null)
            {
                throw new InvalidOperationException("SessionArchiver not configured. Inject SessionArchiver via constructor.");
            }

            var result = await _archiver.ArchiveAsync(session, ct);

            if (result)
            {
                // 从缓存中移除
                _sessionDataCache.TryRemove(sessionId, out _);

                // 触发 Hook
                _hookManager?.TriggerFireAndForget(
                    "session.archived",
                    sessionId,
                    result: new Dictionary<string, object?> { ["session"] = session });
            }

            return result;
        }

        /// <summary>
        /// Share Session - 分享
        /// </summary>
        public async Task<string> ShareAsync(string sessionId, CancellationToken ct = default)
        {
            var session = Get(sessionId);
            if (session == null)
            {
                throw new InvalidOperationException($"Session not found: {sessionId}");
            }

            if (_sharer == null)
            {
                throw new InvalidOperationException("SessionSharer not configured. Inject SessionSharer via constructor.");
            }

            var shareId = await _sharer.ShareAsync(session, ct);

            // 触发 Hook
            _hookManager?.TriggerFireAndForget(
                "session.shared",
                sessionId,
                result: new Dictionary<string, object?>
                {
                    ["session"] = session,
                    ["shareId"] = shareId
                });

            return shareId;
        }

        /// <summary>
        /// Revert Session - 回滚到指定消息
        /// </summary>
        public async Task<bool> RevertAsync(
            string sessionId, string messageId, CancellationToken ct = default)
        {
            if (_reverter == null)
            {
                throw new InvalidOperationException("SessionReverter not configured. Inject SessionReverter via constructor.");
            }

            var result = await _reverter.RevertAsync(sessionId, messageId, ct);

            if (result)
            {
                // 触发 Hook
                _hookManager?.TriggerFireAndForget(
                    "session.reverted",
                    sessionId,
                    result: new Dictionary<string, object?>
                    {
                        ["messageId"] = messageId
                    });
            }

            return result;
        }

        /// <summary>
        /// 列出所有 Session (全局)
        /// </summary>
        public async Task<IReadOnlyList<SessionMetadata>> ListAllAsync(
            string? partitionId = null, CancellationToken ct = default)
        {
            if (_globalStore != null)
            {
                return await _globalStore.ListAllAsync(partitionId, ct);
            }

            // 降级：从内存缓存生成元数据
            var sessions = _sessionDataCache.Values.AsEnumerable();

            if (partitionId != null)
                sessions = sessions.Where(s => s.PartitionId == partitionId);

            return sessions
                .Select(s => new SessionMetadata
                {
                    Id = s.Id,
                    PartitionId = s.PartitionId,
                    SelectedAgent = s.SelectedAgent,
                    ParentSessionId = s.ParentSessionId,
                    ForkLabel = s.ForkLabel,
                    IsArchived = s.IsArchived,
                    MessageCount = s.MessageCount,
                    CreatedAt = new DateTimeOffset(s.CreatedAt),
                    LastActiveAt = new DateTimeOffset(s.LastActiveAt)
                })
                .OrderByDescending(m => m.LastActiveAt)
                .ToList();
        }

        /// <summary>
        /// 设置会话标题
        /// </summary>
        public async Task SetTitleAsync(string sessionId, string title, CancellationToken ct = default)
        {
            var session = Get(sessionId);
            if (session == null)
            {
                _logger?.LogWarning("会话不存在，无法设置标题: {SessionId}", sessionId);
                return;
            }

            session.Title = title;
            session.UpdatedAt = DateTime.Now;

            // 触发 Updated Hook
            _hookManager?.TriggerFireAndForget(
                HookPoints.Updated,
                session.Id,
                result: new Dictionary<string, object?> { ["session"] = session, ["title"] = title });

            // 自动保存
            if (_store != null)
            {
                await SaveAsync(sessionId);
            }

            _logger?.LogInformation("设置会话标题: SessionId={SessionId}, Title={Title}", sessionId, title);
        }
    }
}