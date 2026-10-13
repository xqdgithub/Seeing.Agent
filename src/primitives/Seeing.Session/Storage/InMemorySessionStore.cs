using Seeing.Session.Core;
using System.Collections.Concurrent;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 基于内存的会话存储实现
    /// 使用 ConcurrentDictionary 实现线程安全的内存存储，适用于测试和临时场景
    /// </summary>
    public class InMemorySessionStore : ISessionStore, ISessionSummaryProvider, ISessionArchiveStore
    {
        private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
        private readonly ConcurrentDictionary<string, SessionData> _archived = new();

        /// <summary>
        /// 保存单个会话（<paramref name="data"/> 为调用方移交的私有快照，直接存引用，不克隆）。
        /// </summary>
        public Task SaveAsync(SessionData data, CancellationToken ct = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (string.IsNullOrWhiteSpace(data.Id))
            {
                throw new ArgumentException("会话ID不能为空", nameof(data.Id));
            }

            ct.ThrowIfCancellationRequested();

            // 快照契约：时间戳由调用方设置，仅在 CreatedAt 缺省时补齐
            if (data.CreatedAt == default)
            {
                data.CreatedAt = DateTime.Now;
            }

            _sessions[data.Id] = data;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载单个会话
        /// </summary>
        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }

            ct.ThrowIfCancellationRequested();

            _sessions.TryGetValue(sessionId, out var data);
            return Task.FromResult(data);
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }

            ct.ThrowIfCancellationRequested();

            _sessions.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 列出所有会话
        /// </summary>
        public IAsyncEnumerable<SessionData> ListAsync(CancellationToken ct = default) =>
            EnumerateSessions(ct);

        /// <summary>
        /// 枚举所有会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessions(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var session in _sessions.Values)
            {
                ct.ThrowIfCancellationRequested();
                yield return session;
            }
        }

        /// <summary>
        /// 按分区和代理查询会话
        /// </summary>
        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default) =>
            EnumerateSessionsFiltered(partitionId, agentId, ct);

        /// <summary>
        /// 枚举过滤后的会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessionsFiltered(
            string partitionId,
            string agentId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var session in EnumerateSessions(ct))
            {
                var matchPartition = string.IsNullOrEmpty(partitionId) ||
                                     session.PartitionId == partitionId;

                var matchAgent = string.IsNullOrEmpty(agentId) ||
                                 (session.SelectedAgent == agentId);

                if (matchPartition && matchAgent)
                {
                    yield return session;
                }
            }
        }

        /// <summary>
        /// 批量保存会话
        /// </summary>
        public async Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            foreach (var session in data)
            {
                await SaveAsync(session, ct);
            }
        }

        /// <summary>
        /// 批量加载会话
        /// </summary>
        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) =>
            ListAsync(ct);

        // ============================ 摘要端口 ============================

        /// <inheritdoc/>
        public async IAsyncEnumerable<SessionSummary> ListSummariesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var session in _sessions.Values)
            {
                ct.ThrowIfCancellationRequested();
                yield return SessionSummary.FromSession(session);
            }
        }

        // ============================ 归档端口 ============================

        /// <inheritdoc/>
        public Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));

            ct.ThrowIfCancellationRequested();

            if (!_sessions.TryRemove(sessionId, out var data))
                return Task.FromResult(false);

            data.Metadata[SessionMetadataKeys.PreArchiveStatus] = ((int)data.Status).ToString();
            data.IsArchived = true;
            data.ArchivedAt = DateTimeOffset.Now;
            data.Status = SessionStatus.Archived;
            _archived[sessionId] = data;
            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));

            ct.ThrowIfCancellationRequested();

            if (!_archived.TryRemove(sessionId, out var data))
                return Task.FromResult(false);

            data.IsArchived = false;
            data.ArchivedAt = null;
            data.Status = data.Metadata.TryGetValue(SessionMetadataKeys.PreArchiveStatus, out var preStatus)
                && int.TryParse(preStatus, out var preStatusValue)
                && Enum.IsDefined(typeof(SessionStatus), preStatusValue)
                    ? (SessionStatus)preStatusValue
                    : SessionStatus.Active;
            data.Metadata.Remove(SessionMetadataKeys.PreArchiveStatus);
            data.UpdatedAt = DateTime.Now;
            data.LastActiveAt = DateTime.Now;
            _sessions[sessionId] = data;
            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<SessionSummary> ListArchivedAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var session in _archived.Values)
            {
                ct.ThrowIfCancellationRequested();
                yield return SessionSummary.FromSession(session);
            }
        }

        /// <summary>
        /// 清空所有会话（用于测试）
        /// </summary>
        public void Clear()
        {
            _sessions.Clear();
            _archived.Clear();
        }

        /// <summary>
        /// 获取会话数量（用于测试）
        /// </summary>
        public int Count => _sessions.Count;
    }
}