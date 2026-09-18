namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话组管理器：会话关系（父子 / 分支 / 交接）的唯一权威。
    /// <para>依赖 <c>ISessionManager</c> 与存储端口；不依赖任何执行端口。</para>
    /// </summary>
    public interface ISessionGroupManager
    {
        // === 关系编排（创建 + 入组） ===

        /// <summary>创建根会话及其单成员组。</summary>
        Task<SessionData> CreateRootAsync(
            string? partitionId, string? agent, string? scenario,
            string? title, string? workingDirectory, CancellationToken ct = default);

        /// <summary>创建子 Agent 会话并加入父会话所在组（Relation=Child）。</summary>
        Task<SessionData> CreateChildAsync(
            string parentId, string agentName, string title,
            IReadOnlyList<SessionPermissionRule> snapshot, string? scenario,
            CancellationToken ct = default);

        /// <summary>
        /// 分支会话：源为子会话时建立独立新组；源为根会话时加入源组（Relation=Fork）。
        /// </summary>
        Task<SessionData> ForkSessionAsync(string sessionId, string? title, CancellationToken ct = default);

        /// <summary>备份分支：始终加入源会话所在组（Relation=Fork，附 Label）。</summary>
        Task<SessionData> CreateBackupForkAsync(string sessionId, string label, CancellationToken ct = default);

        /// <summary>创建交接后继（Clean Root）并置为组 active，继承源会话配置字段。</summary>
        Task<SessionData> CreateHandoffSuccessorAsync(
            string sourceSessionId, string? agent, string? title, string? scenario,
            CancellationToken ct = default);

        // === 组管理 ===

        /// <summary>确保会话所属组存在（不存在则建立单成员组），返回该组。</summary>
        Task<SessionGroup> EnsureForSessionAsync(string sessionId, CancellationToken ct = default);

        /// <summary>按组 ID 获取组。</summary>
        Task<SessionGroup?> GetGroupAsync(string groupId, CancellationToken ct = default);

        /// <summary>获取会话所属组（支持缓存与存储冷兜底）。</summary>
        Task<SessionGroup?> GetGroupForSessionAsync(string sessionId, CancellationToken ct = default);

        /// <summary>向组内添加成员（Order 由管理器在组锁内分配）。</summary>
        Task AddMemberAsync(string groupId, SessionGroupMember member, CancellationToken ct = default);

        /// <summary>移除成员；必要时回退 active、提升锚点、或删除空组。</summary>
        Task RemoveMemberAsync(string groupId, string sessionId, CancellationToken ct = default);

        /// <summary>设置组内活跃会话。</summary>
        Task SetActiveAsync(string groupId, string sessionId, CancellationToken ct = default);

        /// <summary>移除会话及其 Child 子树（不取消执行）；必要时提升锚点或删除空组。</summary>
        Task RemoveSessionAsync(string sessionId, CancellationToken ct = default);

        /// <summary>清空内存缓存（组 + 会话→组映射）；用于工作区切换等场景，下次访问时从存储重新加载。</summary>
        void ClearCache();

        // === 查询 ===

        /// <summary>获取会话在组内的父会话 ID。</summary>
        Task<string?> GetParentAsync(string sessionId, CancellationToken ct = default);

        /// <summary>列出指定父会话的 Child 子会话（含冷兜底）。</summary>
        Task<IReadOnlyList<SessionData>> ListChildrenAsync(string parentId, CancellationToken ct = default);

        /// <summary>列出组内成员（返回快照副本）。</summary>
        Task<IReadOnlyList<SessionGroupMember>> ListMembersAsync(string groupId, CancellationToken ct = default);

        /// <summary>列出锚点会话（对未入组会话物化单成员组）。</summary>
        Task<IReadOnlyList<SessionData>> ListAnchorsAsync(string? partitionId = null, CancellationToken ct = default);

        /// <summary>组发生变更时触发（携带组锁内快照，在锁外触发）。</summary>
        event EventHandler<SessionGroupChangedEventArgs>? Changed;
    }
}
