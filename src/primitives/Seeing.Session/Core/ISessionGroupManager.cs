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
        /// 创建平台内部会话（复盘 / 定时任务 / 心跳等）并加入父会话所在组。
        /// <para>与 <see cref="CreateChildAsync"/> 的唯一差异是来源标记：本入口由调用方显式指定
        /// <paramref name="origin"/>；标题保留行为两者一致（都写入调用方给定标题）。</para>
        /// <para>产出的会话 <see cref="SessionData.Kind"/> 仍为 <see cref="SessionKind.SubAgent"/>，
        /// 以满足组不变量「Relation=Child ⇔ Kind=SubAgent」；用途由
        /// <see cref="SessionData.Origin"/> 表达，两者正交。</para>
        /// </summary>
        /// <param name="parentId">父会话 ID（其所在组为新会话的归属组）。</param>
        /// <param name="agentName">子会话使用的 Agent 名。</param>
        /// <param name="title">会话标题。</param>
        /// <param name="snapshot">权限快照。</param>
        /// <param name="scenario">会话级场景名；null = 进程级回退。</param>
        /// <param name="origin">
        /// 会话来源/用途标记。<b>禁止传 <see cref="SessionOrigin.User"/></b>（且必须是已定义枚举值）：
        /// 用户来源应走用户建会话路径；本入口产出 <c>Kind=SubAgent</c> 的会话若标成
        /// <see cref="SessionOrigin.User"/>，会在后续任务视图白名单
        /// （<c>Kind==SubAgent &amp;&amp; Origin==SubAgentTask</c>）下整体消失且难以排查。
        /// </param>
        /// <param name="ct">取消令牌。</param>
        /// <exception cref="ArgumentException"><paramref name="origin"/> 为
        /// <see cref="SessionOrigin.User"/> 或未定义枚举值。</exception>
        Task<SessionData> CreateInternalAsync(
            string parentId, string agentName, string title,
            IReadOnlyList<SessionPermissionRule> snapshot, string? scenario,
            SessionOrigin origin, CancellationToken ct = default);

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
        /// <remarks>
        /// 组不存在或会话不在组内时抛 <see cref="InvalidOperationException"/>（数据不一致属于契约违背，
        /// 不应被静默吞掉）。UI 需要在切换失败时回滚并提示，请自行捕获后处理，见会话页 <c>OnTileClick</c>。
        /// </remarks>
        Task SetActiveAsync(string groupId, string sessionId, CancellationToken ct = default);

        /// <summary>移除会话及其 Child 子树（不取消执行）；必要时提升锚点或删除空组。</summary>
        Task RemoveSessionAsync(string sessionId, CancellationToken ct = default);

        /// <summary>清空内存缓存（组 + 会话→组映射）；用于工作区切换等场景，下次访问时从存储重新加载。</summary>
        void ClearCache();

        // === 查询 ===

        /// <summary>获取会话在组内的父会话 ID。</summary>
        Task<string?> GetParentAsync(string sessionId, CancellationToken ct = default);

        /// <summary>
        /// 尽力同步获取父会话 ID（仅内存索引命中；未命中/无父返回 <c>false</c>）。
        /// <para>供权限生效开关的实时父链解析等高频只读路径使用，不做冷存兜底。</para>
        /// </summary>
        bool TryGetParent(string sessionId, out string? parentId);

        /// <summary>列出指定父会话的 Child 子会话（含冷兜底）。</summary>
        Task<IReadOnlyList<SessionData>> ListChildrenAsync(string parentId, CancellationToken ct = default);

        /// <summary>列出组内成员（返回快照副本）。</summary>
        Task<IReadOnlyList<SessionGroupMember>> ListMembersAsync(string groupId, CancellationToken ct = default);

        /// <summary>枚举全部会话组（轻量关系元数据，不含消息体）。</summary>
        IAsyncEnumerable<SessionGroup> ListGroupsAsync(CancellationToken ct = default);

        /// <summary>列出锚点会话（对未入组会话物化单成员组）。</summary>
        Task<IReadOnlyList<SessionData>> ListAnchorsAsync(string? partitionId = null, CancellationToken ct = default);

        /// <summary>
        /// 列出锚点会话摘要（轻量，不含消息体；不物化组、不写盘）。
        /// </summary>
        Task<IReadOnlyList<SessionSummary>> ListAnchorSummariesAsync(string? partitionId = null, CancellationToken ct = default);

        /// <summary>按组整体归档：归档组内全部会话（含子 / 分支 / 交接）。</summary>
        Task<bool> ArchiveGroupAsync(string groupId, CancellationToken ct = default);

        /// <summary>恢复归档组：恢复组内全部会话。返回是否至少恢复一个。</summary>
        Task<bool> RestoreGroupAsync(string groupId, CancellationToken ct = default);

        /// <summary>组发生变更时触发（携带组锁内快照，在锁外触发）。</summary>
        event EventHandler<SessionGroupChangedEventArgs>? Changed;
    }
}
