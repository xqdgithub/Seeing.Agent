using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话组存储抽象：按组 ID 持久化 <see cref="SessionGroup"/>，
    /// 并支持按成员会话反查所属组。
    /// </summary>
    public interface ISessionGroupStore
    {
        /// <summary>按组 ID 加载；不存在返回 null。</summary>
        Task<SessionGroup?> LoadAsync(string groupId);

        /// <summary>按成员会话 ID 反查所属组；无匹配返回 null。</summary>
        Task<SessionGroup?> FindBySessionAsync(string sessionId);

        /// <summary>保存（覆盖）会话组。</summary>
        Task SaveAsync(SessionGroup group);

        /// <summary>删除会话组。</summary>
        Task DeleteAsync(string groupId);

        /// <summary>枚举全部会话组。</summary>
        IAsyncEnumerable<SessionGroup> ListAsync();
    }
}
