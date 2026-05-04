using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Storage;

namespace Seeing.Session.Management
{
    /// <summary>
    /// Session 管理器 - 管理会话的创建、存储和检索
    /// </summary>
    /// <remarks>
    /// 新架构设计：
    /// - 使用 SessionData + 可选组件注入 + SaveAsync/LoadAsync/Compress
    /// - 使用 IHookManager 触发生命周期钩子
    /// - 保持内存管理模式（ConcurrentDictionary）
    /// </remarks>
    public class SessionManager : ISessionManager
    {
        private readonly ISessionStore? _store;
        private readonly ICompressionStrategy? _compressor;
        private readonly IHookManager? _hookManager;
        private readonly ILogger<SessionManager>? _logger;
        private readonly ConcurrentDictionary<string, SessionData> _sessionDataCache = new();

        /// <summary>
        /// 创建 SessionManager 实例
        /// </summary>
        /// <param name="store">会话存储（可选）</param>
        /// <param name="compressor">压缩策略（可选，默认 SlidingWindowCompression）</param>
        /// <param name="hookManager">Hook 管理器（可选）</param>
        /// <param name="logger">日志（可选）</param>
        public SessionManager(
            ISessionStore? store = null,
            ICompressionStrategy? compressor = null,
            IHookManager? hookManager = null,
            ILogger<SessionManager>? logger = null)
        {
            _store = store;
            _compressor = compressor ?? new SlidingWindowCompression();
            _hookManager = hookManager;
            _logger = logger;
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
                session.Id, partitionId ?? "default", selectedAgent ?? "primary");
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
    }
}