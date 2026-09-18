using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话存储抽象：持久化与查询 <see cref="SessionData"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>快照契约（关键）：</b><paramref name="data"/> 是调用方移交的私有快照。
    /// </para>
    /// <list type="bullet">
    /// <item>实现方不得修改该实例（唯一例外：<c>CreatedAt</c> 为默认值时补齐），也不得依赖调用方后续对活会话的修改；可直接序列化/存储该实例。</item>
    /// <item>调用方保证在 <c>SaveAsync</c> 返回后不再修改传入实例。</item>
    /// <item>时间戳由调用方设置：调用方在克隆前写入活会话的 <c>UpdatedAt</c>（见 SessionManager）；
    /// 实现方仅可在 <c>CreatedAt</c> 为默认值时补齐，不得改写 <c>UpdatedAt</c>。</item>
    /// <item>因此克隆恰好一次，发生在调用方（<c>SessionManager</c>）；后端不再克隆。
    /// <see cref="InMemorySessionStore"/> 直接存该快照引用，<see cref="FileSessionStore"/> 直接序列化该快照。</item>
    /// </list>
    /// </remarks>
    public interface ISessionStore
    {
        /// <summary>持久化单个会话（<paramref name="data"/> 为调用方私有快照）。</summary>
        Task SaveAsync(SessionData data, CancellationToken ct = default);

        /// <summary>按会话 ID 加载单个会话；不存在返回 null。</summary>
        Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default);

        /// <summary>按会话 ID 删除。</summary>
        Task DeleteAsync(string sessionId, CancellationToken ct = default);

        /// <summary>异步枚举全部会话。</summary>
        IAsyncEnumerable<SessionData> ListAsync(CancellationToken ct = default);

        /// <summary>按分区与 Agent 过滤，异步枚举匹配的会话。</summary>
        IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default);

        /// <summary>批量保存会话。</summary>
        Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default);

        /// <summary>批量加载全部会话。</summary>
        IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default);
    }
}
