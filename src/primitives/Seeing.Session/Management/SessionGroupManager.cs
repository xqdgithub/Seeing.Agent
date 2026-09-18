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
            ILogger<SessionGroupManager>? logger = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? NullLogger<SessionGroupManager>.Instance;
            _forker = new SessionForker(NullLogger<SessionForker>.Instance, _sessions);
        }

        // ============================ 组管理 ============================

        /// <inheritdoc/>
        public async Task<SessionGroup> EnsureForSessionAsync(string sessionId, CancellationToken ct = default)
        {
            var sessionLock = GetLock(_sessionLocks, sessionId);
            await sessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = await GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
                if (existing != null)
                    return existing;

                var session = _sessions.Get(sessionId)
                    ?? await _sessions.LoadAsync(sessionId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session not found: {sessionId}");

                var group = NewSingleMemberGroup(session);
                await _store.SaveAsync(group).ConfigureAwait(false);
                CacheGroup(group);
                await _sessions.UpdateSessionAsync(sessionId, s => s.GroupId = group.Id, ct).ConfigureAwait(false);

                _logger.LogInformation("为会话创建组: SessionId={SessionId}, GroupId={GroupId}", sessionId, group.Id);
                return group;
            }
            finally
            {
                sessionLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<SessionGroup?> GetGroupAsync(string groupId, CancellationToken ct = default)
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

        /// <inheritdoc/>
        public async Task<SessionGroup?> GetGroupForSessionAsync(string sessionId, CancellationToken ct = default)
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

            var groupLock = GetLock(_groupLocks, groupId);
            await groupLock.WaitAsync(ct).ConfigureAwait(false);
            SessionGroup snapshot;
            try
            {
                var group = await GetGroupAsync(groupId, ct).ConfigureAwait(false)
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
                await _store.SaveAsync(group).ConfigureAwait(false);
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
                var group = await GetGroupAsync(groupId, ct).ConfigureAwait(false);
                if (group == null)
                    return;

                var member = group.Members.FirstOrDefault(m => m.SessionId == sessionId);
                if (member == null)
                    return;

                var wasAnchor = member.IsAnchor || group.AnchorSessionId == sessionId;
                group.Members.Remove(member);
                UnmapSession(sessionId, group.Id);

                if (group.ActiveSessionId == sessionId)
                    group.ActiveSessionId = null;
                if (wasAnchor)
                    PromoteAnchor(group);

                Touch(group);
                if (group.Members.Count == 0)
                {
                    await _store.DeleteAsync(group.Id).ConfigureAwait(false);
                    RemoveFromCache(group.Id);
                }
                else
                {
                    await _store.SaveAsync(group).ConfigureAwait(false);
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
                var group = await GetGroupAsync(groupId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Session group not found: {groupId}");

                if (!group.Members.Any(m => m.SessionId == sessionId))
                    throw new InvalidOperationException($"Session {sessionId} is not a member of group {groupId}");

                group.ActiveSessionId = sessionId;
                Touch(group);
                await _store.SaveAsync(group).ConfigureAwait(false);
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
            var group = await GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
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
                var current = await GetGroupAsync(group.Id, ct).ConfigureAwait(false) ?? group;

                var toRemove = new HashSet<string> { sessionId };
                CollectChildSubtree(current, sessionId, toRemove);

                var anchorMember = current.Members.FirstOrDefault(m => m.SessionId == sessionId);
                var wasAnchor = anchorMember?.IsAnchor == true || current.AnchorSessionId == sessionId;

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
                if (wasAnchor)
                    PromoteAnchor(current);

                Touch(current);
                if (current.Members.Count == 0)
                {
                    await _store.DeleteAsync(current.Id).ConfigureAwait(false);
                    RemoveFromCache(current.Id);
                }
                else
                {
                    await _store.SaveAsync(current).ConfigureAwait(false);
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
            var group = await GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
            return group?.Members.FirstOrDefault(m => m.SessionId == sessionId)?.ParentSessionId;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SessionData>> ListChildrenAsync(string parentId, CancellationToken ct = default)
        {
            var childIds = new List<string>();
            foreach (var group in _cache.Values)
                childIds.AddRange(SelectChildIds(group, parentId));

            if (childIds.Count == 0)
            {
                var found = await _store.FindBySessionAsync(parentId).ConfigureAwait(false);
                if (found != null)
                {
                    CacheGroup(found);
                    childIds.AddRange(SelectChildIds(found, parentId));
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
            var group = await GetGroupAsync(groupId, ct).ConfigureAwait(false);
            return group == null
                ? Array.Empty<SessionGroupMember>()
                : group.Members.Select(m => m.Clone()).ToList();
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
            await _store.SaveAsync(group).ConfigureAwait(false);
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
                await _store.SaveAsync(group).ConfigureAwait(false);
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

            await _sessions.SaveAsync(successor.Id).ConfigureAwait(false);

            var group = await EnsureForSessionAsync(sourceSessionId, ct).ConfigureAwait(false);
            await AddMemberAsync(group.Id, new SessionGroupMember
            {
                SessionId = successor.Id,
                Relation = SessionRelation.HandoffSuccessor,
                ParentSessionId = sourceSessionId
            }, ct).ConfigureAwait(false);

            await _sessions.UpdateSessionAsync(successor.Id, s => s.GroupId = group.Id, ct).ConfigureAwait(false);
            await SetActiveAsync(group.Id, successor.Id, ct).ConfigureAwait(false);
            await _sessions.SaveAsync(successor.Id).ConfigureAwait(false);

            return successor;
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

        private static void PromoteAnchor(SessionGroup group)
        {
            var next = group.Members.OrderBy(m => m.Order).FirstOrDefault();
            if (next == null)
                return;

            foreach (var member in group.Members)
                member.IsAnchor = false;

            next.IsAnchor = true;
            next.Relation = SessionRelation.None;
            group.AnchorSessionId = next.SessionId;
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
