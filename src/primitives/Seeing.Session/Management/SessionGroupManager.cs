using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Storage;

namespace Seeing.Session.Management
{
    /// <summary>
    /// 会话组管理器：关系（父子 / 分支 / 交接）的唯一权威。
    /// <para>依赖 <see cref="ISessionManager"/> + <see cref="ISessionGroupStore"/>，不依赖任何执行端口。</para>
    /// <para>并发模型：两级锁（会话锁 → 组锁）；<c>Changed</c> 在锁外触发且携带组锁内快照。</para>
    /// </summary>
    public sealed class SessionGroupManager : ISessionGroupManager
    {
        private readonly ISessionManager _sessions;
        private readonly ISessionGroupStore _store;
        private readonly ILogger<SessionGroupManager> _logger;
        private readonly SessionForker _forker;

        private readonly ConcurrentDictionary<string, SessionGroup> _cache = new();
        private readonly ConcurrentDictionary<string, string> _sessionToGroup = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();

        /// <inheritdoc/>
        public event EventHandler<SessionGroupChangedEventArgs>? Changed;

        public SessionGroupManager(
            ISessionManager sessions,
            ISessionGroupStore store,
            SessionForker forker,
            ILogger<SessionGroupManager>? logger = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _forker = forker ?? throw new ArgumentNullException(nameof(forker));
            _logger = logger ?? NullLogger<SessionGroupManager>.Instance;
        }

        // ============================ 组管理 ============================

        /// <inheritdoc/>
        public async Task<SessionGroup> EnsureForSessionAsync(string sessionId, CancellationToken ct = default)
        {
            var sessionLock = GetLock(_sessionLocks, sessionId);
            await sessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = await GetGroupForSessionLiveAsync(sessionId, ct).ConfigureAwait(false);
                if (existing != null)
                    return existing.Clone();

                var session = _sessions.Get(sessionId)
                    ?? await _sessions.LoadAsync(sessionId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session not found: {sessionId}");

                var group = NewSingleMemberGroup(session);
                await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
                CacheGroup(group);
                await _sessions.UpdateSessionAsync(sessionId, s => s.GroupId = group.Id, ct).ConfigureAwait(false);

                _logger.LogInformation("为会话创建组: SessionId={SessionId}, GroupId={GroupId}", sessionId, group.Id);
                return group.Clone();
            }
            finally
            {
                sessionLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<SessionGroup?> GetGroupAsync(string groupId, CancellationToken ct = default)
        {
            var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false);
            return group?.Clone();
        }

        /// <inheritdoc/>
        public async Task<SessionGroup?> GetGroupForSessionAsync(string sessionId, CancellationToken ct = default)
        {
            var group = await GetGroupForSessionLiveAsync(sessionId, ct).ConfigureAwait(false);
            return group?.Clone();
        }

        /// <inheritdoc/>
        public void ClearCache()
        {
            _cache.Clear();
            _sessionToGroup.Clear();
        }

        /// <summary>
        /// 内部读：返回缓存中的活组对象（供持锁写路径与快照读取使用）。
        /// 公开读 API 一律基于此克隆，避免调用方与写者并发遍历。
        /// </summary>
        private async Task<SessionGroup?> GetGroupLiveAsync(string groupId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(groupId))
                return null;

            if (_cache.TryGetValue(groupId, out var cached))
                return cached;

            var loaded = await _store.LoadAsync(groupId).ConfigureAwait(false);
            if (loaded != null)
                CacheGroup(loaded);
            return loaded;
        }

        /// <summary>内部读：返回会话所属的活组对象（缓存 → 会话 GroupId → 存储反查）。</summary>
        private async Task<SessionGroup?> GetGroupForSessionLiveAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;

            if (_sessionToGroup.TryGetValue(sessionId, out var mapped)
                && _cache.TryGetValue(mapped, out var mappedGroup))
            {
                return mappedGroup;
            }

            var session = _sessions.Get(sessionId);
            if (session?.GroupId is { Length: > 0 } groupId)
            {
                if (_cache.TryGetValue(groupId, out var cachedBySession))
                    return cachedBySession;

                var loadedById = await _store.LoadAsync(groupId).ConfigureAwait(false);
                if (loadedById != null)
                {
                    CacheGroup(loadedById);
                    return loadedById;
                }
            }

            var found = await _store.FindBySessionAsync(sessionId).ConfigureAwait(false);
            if (found != null)
                CacheGroup(found);
            return found;
        }

        /// <inheritdoc/>
        public async Task AddMemberAsync(string groupId, SessionGroupMember member, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(member);
            ValidateMemberInvariant(member);

            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            SessionGroup snapshot;
            try
            {
                var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session group not found: {groupId}");

                var existing = group.Members.FirstOrDefault(m => m.SessionId == member.SessionId);
                if (existing != null)
                {
                    existing.Relation = member.Relation;
                    existing.ParentSessionId = member.ParentSessionId;
                    existing.Label = member.Label;
                    existing.IsAnchor = member.IsAnchor;
                }
                else
                {
                    member.Order = group.Members.Count == 0 ? 0 : group.Members.Max(m => m.Order) + 1;
                    group.Members.Add(member);
                }

                Touch(group);
                await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
                _sessionToGroup[member.SessionId] = group.Id;
                snapshot = group.Clone();
            }
            finally
            {
                groupLock.Release();
            }

            RaiseChanged(snapshot);
        }

        /// <inheritdoc/>
        public async Task RemoveMemberAsync(string groupId, string sessionId, CancellationToken ct = default)
        {
            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            SessionGroup snapshot;
            try
            {
                var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false);
                if (group == null)
                    return;

                var member = group.Members.FirstOrDefault(m => m.SessionId == sessionId);
                if (member == null)
                    return;

                var wasAnchor = member.IsAnchor || group.AnchorSessionId == sessionId;
                var deletedParent = member.ParentSessionId;

                // 移除前重挂：主线后继与 Fork/Child 的来源指向被删节点时，改指被删节点的父（可能为 null）
                foreach (var m in group.Members)
                {
                    if (m.SessionId == sessionId) continue;
                    if (!string.Equals(m.ParentSessionId, sessionId, StringComparison.Ordinal)) continue;
                    m.ParentSessionId = deletedParent;
                }

                group.Members.Remove(member);
                UnmapSession(sessionId, group.Id);

                if (group.ActiveSessionId == sessionId)
                    group.ActiveSessionId = null;

                // 全路径归一：删除锚点时优先回溯到被删节点的父；否则为幂等 no-op
                NormalizeAnchor(group, wasAnchor ? deletedParent : null);
                group.Title = _sessions.Get(group.AnchorSessionId)?.Title ?? group.Title;

                Touch(group);
                if (group.Members.Count == 0)
                {
                    await _store.DeleteAsync(group.Id, ct).ConfigureAwait(false);
                    RemoveFromCache(group.Id);
                }
                else
                {
                    await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
                }

                snapshot = group.Clone();
            }
            finally
            {
                groupLock.Release();
            }

            RaiseChanged(snapshot);
        }

        /// <inheritdoc/>
        public async Task SetActiveAsync(string groupId, string sessionId, CancellationToken ct = default)
        {
            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            SessionGroup snapshot;
            try
            {
                var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session group not found: {groupId}");

                if (!group.Members.Any(m => m.SessionId == sessionId))
                    throw new InvalidOperationException($"Session {sessionId} is not a member of group {groupId}");

                group.ActiveSessionId = sessionId;
                Touch(group);
                await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
                snapshot = group.Clone();
            }
            finally
            {
                groupLock.Release();
            }

            RaiseChanged(snapshot);
        }

        /// <inheritdoc/>
        public async Task RemoveSessionAsync(string sessionId, CancellationToken ct = default)
        {
            var group = await GetGroupForSessionLiveAsync(sessionId, ct).ConfigureAwait(false);
            if (group == null)
            {
                _sessions.Delete(sessionId);
                return;
            }

            var groupLock = GetLock(_groupLocks, group.Id);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            var removedSessionIds = new List<string>();
            SessionGroup snapshot;
            try
            {
                var current = await GetGroupLiveAsync(group.Id, ct).ConfigureAwait(false) ?? group;

                var toRemove = new HashSet<string> { sessionId };
                CollectChildSubtree(current, sessionId, toRemove);

                var anchorMember = current.Members.FirstOrDefault(m => m.SessionId == sessionId);
                var wasAnchor = anchorMember?.IsAnchor == true || current.AnchorSessionId == sessionId;
                var deletedParent = anchorMember?.ParentSessionId;

                // 移除前重挂：不属于删除集、且 ParentSessionId 指向任一被删节点的成员，
                // 改挂到"该被删节点最近的、不在删除集内的祖先"（若不存在则置 null），与 RemoveMemberAsync 对称。
                // 多层 Child 子树删除时，被删节点的父可能同样在 toRemove 内，须沿 ParentSessionId 向上解析。
                foreach (var m in current.Members)
                {
                    if (toRemove.Contains(m.SessionId)) continue;
                    if (string.IsNullOrEmpty(m.ParentSessionId) || !toRemove.Contains(m.ParentSessionId!)) continue;

                    var resolvedParent = m.ParentSessionId;
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (!string.IsNullOrEmpty(resolvedParent)
                           && toRemove.Contains(resolvedParent!)
                           && visited.Add(resolvedParent!))
                    {
                        var deletedNode = current.Members.FirstOrDefault(x => x.SessionId == resolvedParent);
                        resolvedParent = deletedNode?.ParentSessionId;
                    }

                    m.ParentSessionId = resolvedParent;
                }

                foreach (var id in toRemove)
                {
                    var member = current.Members.FirstOrDefault(m => m.SessionId == id);
                    if (member != null)
                        current.Members.Remove(member);
                    UnmapSession(id, current.Id);
                    removedSessionIds.Add(id);
                }

                if (current.ActiveSessionId != null && toRemove.Contains(current.ActiveSessionId))
                    current.ActiveSessionId = null;

                // 全路径归一（锚点删除时优先回溯到被删节点的父；否则幂等 no-op）
                NormalizeAnchor(current, wasAnchor ? deletedParent : null);
                current.Title = _sessions.Get(current.AnchorSessionId)?.Title ?? current.Title;

                Touch(current);
                if (current.Members.Count == 0)
                {
                    await _store.DeleteAsync(current.Id, ct).ConfigureAwait(false);
                    RemoveFromCache(current.Id);
                }
                else
                {
                    await _store.SaveAsync(current.Clone(), ct).ConfigureAwait(false);
                }

                snapshot = current.Clone();
            }
            finally
            {
                groupLock.Release();
            }

            foreach (var id in removedSessionIds)
                _sessions.Delete(id);

            RaiseChanged(snapshot);
        }

        // ============================ 查询 ============================

        /// <inheritdoc/>
        public async Task<string?> GetParentAsync(string sessionId, CancellationToken ct = default)
        {
            var group = await GetGroupForSessionLiveAsync(sessionId, ct).ConfigureAwait(false);
            if (group == null)
                return null;

            var groupLock = GetLock(_groupLocks, group.Id);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = await GetGroupLiveAsync(group.Id, ct).ConfigureAwait(false) ?? group;
                return current.Members.FirstOrDefault(m => m.SessionId == sessionId)?.ParentSessionId;
            }
            finally
            {
                groupLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SessionData>> ListChildrenAsync(string parentId, CancellationToken ct = default)
        {
            var childIds = new List<string>();
            foreach (var groupId in _cache.Keys.ToList())
                childIds.AddRange(await SelectChildIdsLockedAsync(groupId, parentId, ct).ConfigureAwait(false));

            if (childIds.Count == 0)
            {
                var found = await _store.FindBySessionAsync(parentId).ConfigureAwait(false);
                if (found != null)
                {
                    CacheGroup(found);
                    childIds.AddRange(await SelectChildIdsLockedAsync(found.Id, parentId, ct).ConfigureAwait(false));
                }
            }

            var result = new List<SessionData>();
            foreach (var id in childIds.Distinct())
            {
                var session = _sessions.Get(id) ?? await _sessions.LoadAsync(id).ConfigureAwait(false);
                if (session != null)
                    result.Add(session);
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SessionGroupMember>> ListMembersAsync(string groupId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(groupId))
                return Array.Empty<SessionGroupMember>();

            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false);
                return group == null
                    ? Array.Empty<SessionGroupMember>()
                    : group.Members.Select(m => m.Clone()).ToList();
            }
            finally
            {
                groupLock.Release();
            }
        }

        /// <summary>在组锁内读取活组的 Child 子会话 ID，避免与写者并发遍历 Members。</summary>
        private async Task<IReadOnlyList<string>> SelectChildIdsLockedAsync(
            string groupId, string parentId, CancellationToken ct)
        {
            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var group = await GetGroupLiveAsync(groupId, ct).ConfigureAwait(false);
                return group == null
                    ? Array.Empty<string>()
                    : SelectChildIds(group, parentId).ToList();
            }
            finally
            {
                groupLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SessionData>> ListAnchorsAsync(
            string? partitionId = null, CancellationToken ct = default)
        {
            var all = await _sessions.LoadAllFromStorageAsync(ct).ConfigureAwait(false);

            foreach (var session in all.Where(s => string.IsNullOrEmpty(s.GroupId)))
                await EnsureForSessionAsync(session.Id, ct).ConfigureAwait(false);

            var seen = new HashSet<string>();
            var result = new List<SessionData>();
            foreach (var session in all)
            {
                if (session.IsArchived)
                    continue;
                if (partitionId != null && session.PartitionId != partitionId)
                    continue;
                if (!seen.Add(session.Id))
                    continue;

                var group = await GetGroupForSessionAsync(session.Id, ct).ConfigureAwait(false);
                if (group != null && group.AnchorSessionId == session.Id)
                    result.Add(session);
            }

            return result;
        }

        // ============================ 关系编排 ============================

        /// <inheritdoc/>
        public async Task<SessionData> CreateRootAsync(
            string? partitionId, string? agent, string? scenario,
            string? title, string? workingDirectory, CancellationToken ct = default)
        {
            var session = _sessions.Create(partitionId, agent, scenario);
            if (!string.IsNullOrEmpty(title))
                session.Title = title;
            if (workingDirectory != null)
                session.WorkingDirectory = workingDirectory;

            var group = NewSingleMemberGroup(session);
            await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
            CacheGroup(group);

            await _sessions.UpdateSessionAsync(session.Id, s =>
            {
                s.Kind = SessionKind.Root;
                s.GroupId = group.Id;
                if (!string.IsNullOrEmpty(title))
                    s.Title = title;
                if (workingDirectory != null)
                    s.WorkingDirectory = workingDirectory;
            }, ct).ConfigureAwait(false);

            return session;
        }

        /// <inheritdoc/>
        public async Task<SessionData> CreateChildAsync(
            string parentId, string agentName, string title,
            IReadOnlyList<SessionPermissionRule> snapshot, string? scenario,
            CancellationToken ct = default)
        {
            var parentGroup = await EnsureForSessionAsync(parentId, ct).ConfigureAwait(false);
            var parent = _sessions.Get(parentId)
                ?? await _sessions.LoadAsync(parentId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session not found: {parentId}");

            var child = _sessions.Create(parent.PartitionId, agentName, scenario);
            child.Kind = SessionKind.SubAgent;
            child.Title = title;
            child.SelectedAgent = agentName;
            // 默认继承父委派时配置；子 Agent 自带 Model 时由调用方覆盖
            child.WorkingDirectory = parent.WorkingDirectory;
            child.SelectedModel = parent.SelectedModel;
            child.SelectedThinkingEffort = parent.SelectedThinkingEffort;
            child.PermissionSnapshot = snapshot is null
                ? new List<SessionPermissionRule>()
                : snapshot.Select(r => new SessionPermissionRule
                {
                    Kind = r.Kind,
                    Pattern = r.Pattern,
                    Effect = r.Effect,
                    Priority = r.Priority
                }).ToList();

            await _sessions.SaveAsync(child.Id).ConfigureAwait(false);

            await AddMemberAsync(parentGroup.Id, new SessionGroupMember
            {
                SessionId = child.Id,
                Relation = SessionRelation.Child,
                ParentSessionId = parentId
            }, ct).ConfigureAwait(false);

            await _sessions.UpdateSessionAsync(child.Id, s => s.GroupId = parentGroup.Id, ct).ConfigureAwait(false);
            return child;
        }

        /// <inheritdoc/>
        public async Task<SessionData> ForkSessionAsync(string sessionId, string? title, CancellationToken ct = default)
        {
            var source = _sessions.Get(sessionId)
                ?? await _sessions.LoadAsync(sessionId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session not found: {sessionId}");

            var forked = await _forker.ForkAsync(sessionId, title, ct).ConfigureAwait(false);
            forked.Kind = SessionKind.Root;

            if (source.Kind == SessionKind.SubAgent)
            {
                // 子会话分支：建立独立新组，仅以 ParentSessionId 记录谱系
                var group = NewSingleMemberGroup(forked, parentSessionId: sessionId);
                await _store.SaveAsync(group.Clone(), ct).ConfigureAwait(false);
                CacheGroup(group);
                await _sessions.UpdateSessionAsync(forked.Id, s => s.GroupId = group.Id, ct).ConfigureAwait(false);
            }
            else
            {
                var group = await EnsureForSessionAsync(sessionId, ct).ConfigureAwait(false);
                await AddMemberAsync(group.Id, new SessionGroupMember
                {
                    SessionId = forked.Id,
                    Relation = SessionRelation.Fork,
                    ParentSessionId = sessionId
                }, ct).ConfigureAwait(false);
                await _sessions.UpdateSessionAsync(forked.Id, s => s.GroupId = group.Id, ct).ConfigureAwait(false);
            }

            await _sessions.SaveAsync(forked.Id).ConfigureAwait(false);
            return forked;
        }

        /// <inheritdoc/>
        public async Task<SessionData> CreateBackupForkAsync(string sessionId, string label, CancellationToken ct = default)
        {
            var source = _sessions.Get(sessionId)
                ?? await _sessions.LoadAsync(sessionId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session not found: {sessionId}");

            var forked = await _forker.ForkAsync(sessionId, label, ct).ConfigureAwait(false);
            forked.Kind = SessionKind.Root;

            // 备份始终加入源会话所在组（即使源为子会话也沿用其组）
            var group = await EnsureForSessionAsync(source.Id, ct).ConfigureAwait(false);
            await AddMemberAsync(group.Id, new SessionGroupMember
            {
                SessionId = forked.Id,
                Relation = SessionRelation.Fork,
                ParentSessionId = sessionId,
                Label = label
            }, ct).ConfigureAwait(false);

            await _sessions.UpdateSessionAsync(forked.Id, s => s.GroupId = group.Id, ct).ConfigureAwait(false);
            await _sessions.SaveAsync(forked.Id).ConfigureAwait(false);
            return forked;
        }

        /// <inheritdoc/>
        public async Task<SessionData> CreateHandoffSuccessorAsync(
            string sourceSessionId, string? agent, string? title, string? scenario,
            CancellationToken ct = default)
        {
            var source = _sessions.Get(sourceSessionId)
                ?? await _sessions.LoadAsync(sourceSessionId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session not found: {sourceSessionId}");

            var successor = _sessions.Create(
                source.PartitionId,
                agent ?? source.SelectedAgent,
                scenario ?? source.Scenario);

            successor.Kind = SessionKind.Root;
            successor.Title = string.IsNullOrEmpty(title) ? source.Title : title;
            successor.WorkingDirectory = source.WorkingDirectory;
            successor.SelectedModel = source.SelectedModel;
            successor.SelectedThinkingEffort = source.SelectedThinkingEffort;

            string? groupId = null;
            try
            {
                await _sessions.SaveAsync(successor.Id).ConfigureAwait(false);

                var group = await EnsureForSessionAsync(sourceSessionId, ct).ConfigureAwait(false);
                groupId = group.Id;

                // 组锁内原子迁移：校验源为锚点 → 建成员 → 归一锚点 → 一次持久化/发布
                var groupLock = GetLock(_groupLocks, group.Id);
                await groupLock.WaitAsync(ct).ConfigureAwait(false);
                SessionGroup snapshot;
                try
                {
                    var current = await GetGroupLiveAsync(group.Id, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Session group not found: {group.Id}");

                    var sourceMember = current.Members.FirstOrDefault(m => m.SessionId == sourceSessionId)
                        ?? throw new InvalidOperationException($"会话不在组内: {sourceSessionId}");
                    if (!(sourceMember.IsAnchor || current.AnchorSessionId == sourceSessionId))
                        throw new InvalidOperationException("仅当前锚点可被交接");

                    sourceMember.IsAnchor = false;
                    sourceMember.Relation = SessionRelation.HandoffPredecessor;

                    var successorMember = new SessionGroupMember
                    {
                        SessionId = successor.Id,
                        Relation = SessionRelation.HandoffSuccessor,
                        ParentSessionId = sourceSessionId,
                        Order = current.Members.Count == 0 ? 0 : current.Members.Max(m => m.Order) + 1
                    };
                    current.Members.Add(successorMember);
                    _sessionToGroup[successor.Id] = current.Id;
                    current.ActiveSessionId = successor.Id;

                    NormalizeAnchor(current, successor.Id);
                    current.Title = _sessions.Get(current.AnchorSessionId)?.Title ?? current.Title;
                    Touch(current);
                    await _store.SaveAsync(current.Clone(), ct).ConfigureAwait(false);
                    snapshot = current.Clone();
                }
                finally
                {
                    groupLock.Release();
                }

                await _sessions.UpdateSessionAsync(successor.Id, s => s.GroupId = group.Id, ct).ConfigureAwait(false);
                await _sessions.SaveAsync(successor.Id).ConfigureAwait(false);

                RaiseChanged(snapshot);
                return successor;
            }
            catch
            {
                // 中途失败自清理：移除已入组/已建会话，避免孤儿泄漏
                await CleanupHandoffSuccessorAsync(successor.Id, groupId, ct).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// 创建交接后继失败时清理：从源组移除可能已写入的成员（含未持久化的活组内存态），
        /// 再删除已建会话。尽力而为，不掩盖原始异常。
        /// </summary>
        private async Task CleanupHandoffSuccessorAsync(
            string successorId, string? groupId, CancellationToken ct)
        {
            if (groupId is not null)
            {
                try
                {
                    var groupLock = GetLock(_groupLocks, groupId);
                    await groupLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        var group = await GetGroupLiveAsync(groupId, CancellationToken.None).ConfigureAwait(false);
                        var member = group?.Members.FirstOrDefault(m => m.SessionId == successorId);
                        if (group is not null && member is not null)
                        {
                            // 移除后继前记录其来源（原锚点），供归一优先级 (a) 复位原锚点
                            var removedParent = member.ParentSessionId;
                            group.Members.Remove(member);
                            UnmapSession(successorId, group.Id);
                            if (group.ActiveSessionId == successorId)
                                group.ActiveSessionId = null;
                            // 恢复原锚点：传 hint 确保退出点仍是原锚点，而非按 Order 回退到链首
                            NormalizeAnchor(group, removedParent);
                            group.Title = _sessions.Get(group.AnchorSessionId)?.Title ?? group.Title;
                            Touch(group);
                            await _store.SaveAsync(group.Clone(), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        groupLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理交接后继组成员失败: SuccessorId={SuccessorId}", successorId);
                }
            }

            _sessions.Delete(successorId);
        }

        // ============================ 内部辅助 ============================

        private static IEnumerable<string> SelectChildIds(SessionGroup group, string parentId) =>
            group.Members
                .Where(m => !m.IsAnchor
                            && m.Relation == SessionRelation.Child
                            && m.ParentSessionId == parentId)
                .Select(m => m.SessionId);

        private static void CollectChildSubtree(SessionGroup group, string parentId, HashSet<string> accumulator)
        {
            var children = group.Members
                .Where(m => !m.IsAnchor
                            && m.Relation == SessionRelation.Child
                            && m.ParentSessionId == parentId)
                .Select(m => m.SessionId)
                .ToList();

            foreach (var childId in children)
            {
                if (accumulator.Add(childId))
                    CollectChildSubtree(group, childId, accumulator);
            }
        }

        /// <summary>
        /// 锚点归一（仅内存态；不持久化/不发布，由调用方统一 Touch/SaveAsync/RaiseChanged）。
        /// 优先级：(a) hint → (b) 当前锚点存活保持 → (d) 非 Fork 且非 Child
        /// → (e) Fork 兜底 → (f) 仅剩 Child 退化。
        /// <para>主线回溯由调用方以 <paramref name="preferredMemberHint"/>（(a)）承担，本方法不再自行回溯。</para>
        /// </summary>
        private static void NormalizeAnchor(SessionGroup group, string? preferredMemberHint = null)
        {
            if (group.Members.Count == 0)
                return;

            SessionGroupMember? ByOrder(Func<SessionGroupMember, bool> pred) =>
                group.Members.Where(pred).OrderBy(m => m.Order).FirstOrDefault();

            SessionGroupMember? target = null;
            if (!string.IsNullOrEmpty(preferredMemberHint))
                target = group.Members.FirstOrDefault(m => m.SessionId == preferredMemberHint);

            if (target is null)
            {
                var current = group.Members.FirstOrDefault(m => m.IsAnchor)
                    ?? group.Members.FirstOrDefault(m => m.SessionId == group.AnchorSessionId);
                if (current is not null)
                    target = current;                              // (b) 幂等
            }

            target ??= ByOrder(m => m.Relation != SessionRelation.Fork && m.Relation != SessionRelation.Child); // (d)
            target ??= ByOrder(m => m.Relation == SessionRelation.Fork);                                          // (e)
            target ??= ByOrder(_ => true);                                                                        // (f)
            if (target is null)
                return;

            foreach (var member in group.Members)
                member.IsAnchor = false;

            target.IsAnchor = true;
            target.Relation = target.Relation == SessionRelation.Child
                ? SessionRelation.Child
                : (target.Relation == SessionRelation.Fork || string.IsNullOrEmpty(target.ParentSessionId)
                    ? SessionRelation.None
                    : SessionRelation.HandoffSuccessor);

            group.AnchorSessionId = target.SessionId;
        }

        /// <summary>
        /// 校验 §4.5.12 成员不变量（非锚点成员双向）：
        /// <c>Relation==Child</c> ⇔ 会话 <see cref="SessionKind.SubAgent"/>；
        /// 锚点成员 <c>Relation ∈ { None, HandoffSuccessor }</c>（<c>Fork</c> 兜底当选时已归一为 <c>None</c>，
        /// <c>Child</c> 仅退化态由 <see cref="NormalizeAnchor"/> 内部保留，不在此入口放行）。
        /// </summary>
        private void ValidateMemberInvariant(SessionGroupMember member)
        {
            if (member.IsAnchor)
            {
                if (member.Relation != SessionRelation.None
                    && member.Relation != SessionRelation.HandoffSuccessor)
                    throw new InvalidOperationException(
                        $"锚点成员必须 Relation=None 或 HandoffSuccessor：SessionId={member.SessionId}, Relation={member.Relation}");
                return;
            }

            var session = _sessions.Get(member.SessionId);

            if (member.Relation == SessionRelation.Child)
            {
                if (session == null || session.Kind != SessionKind.SubAgent)
                    throw new InvalidOperationException(
                        $"非锚点 Child 成员必须对应 SubAgent 会话：SessionId={member.SessionId}, Kind={session?.Kind.ToString() ?? "missing"}");
                return;
            }

            // 反向：SubAgent 会话不得挂 Fork / HandoffSuccessor / None 等非 Child 关系
            if (session?.Kind == SessionKind.SubAgent)
                throw new InvalidOperationException(
                    $"非锚点 SubAgent 成员必须 Relation=Child：SessionId={member.SessionId}, Relation={member.Relation}");
        }

        private static void Touch(SessionGroup group)
        {
            group.Version++;
            group.UpdatedAt = DateTime.Now;
        }

        private static SessionGroup NewSingleMemberGroup(
            SessionData session,
            string? parentSessionId = null,
            SessionRelation relation = SessionRelation.None)
        {
            var now = DateTime.Now;
            return new SessionGroup
            {
                Id = NewGroupId(),
                PartitionId = session.PartitionId,
                Title = session.Title,
                AnchorSessionId = session.Id,
                ActiveSessionId = session.Id,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 1,
                Members = new List<SessionGroupMember>
                {
                    new()
                    {
                        SessionId = session.Id,
                        Relation = relation,
                        ParentSessionId = parentSessionId,
                        IsAnchor = true,
                        Order = 0
                    }
                }
            };
        }

        private static string NewGroupId() =>
            "grp_" + DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];

        private static SemaphoreSlim GetLock(
            ConcurrentDictionary<string, SemaphoreSlim> locks, string key) =>
            locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        private void CacheGroup(SessionGroup group)
        {
            _cache[group.Id] = group;
            foreach (var member in group.Members)
                _sessionToGroup[member.SessionId] = group.Id;
        }

        private void RemoveFromCache(string groupId)
        {
            if (!_cache.TryRemove(groupId, out var group))
                return;

            foreach (var member in group.Members)
                UnmapSession(member.SessionId, groupId);
        }

        private void UnmapSession(string sessionId, string groupId)
        {
            if (_sessionToGroup.TryGetValue(sessionId, out var mapped) && mapped == groupId)
                _sessionToGroup.TryRemove(sessionId, out _);
        }

        private void RaiseChanged(SessionGroup snapshot) =>
            Changed?.Invoke(this, new SessionGroupChangedEventArgs { Group = snapshot });
    }
}
