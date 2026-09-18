using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话组存储抽象：按组 ID 持久化 <see cref="SessionGroup"/>，
    /// 并支持按成员会话反查所属组。
    /// </summary>
    /// <remarks>
    /// <b>快照契约：</b>与 <see cref="ISessionStore"/> 相同——<paramref name="group"/> 为调用方
    /// （<c>SessionGroupManager</c>）移交的私有快照；实现方不得修改该实例，时间戳由调用方在克隆前写入，
    /// 实现方仅在 <c>CreatedAt</c> 缺省时补齐。
    /// </remarks>
    public interface ISessionGroupStore
    {
        /// <summary>按组 ID 加载；不存在返回 null。</summary>
        Task<SessionGroup?> LoadAsync(string groupId, CancellationToken ct = default);

        /// <summary>按成员会话 ID 反查所属组；无匹配返回 null。</summary>
        Task<SessionGroup?> FindBySessionAsync(string sessionId, CancellationToken ct = default);

        /// <summary>保存（覆盖）会话组（<paramref name="group"/> 为调用方私有快照）。</summary>
        Task SaveAsync(SessionGroup group, CancellationToken ct = default);

        /// <summary>删除会话组。</summary>
        Task DeleteAsync(string groupId, CancellationToken ct = default);

        /// <summary>枚举全部会话组。</summary>
        IAsyncEnumerable<SessionGroup> ListAsync(CancellationToken ct = default);
    }
}
