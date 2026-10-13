using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话归档端口：单会话归档原语（移出活跃集、保留可恢复数据）。
    /// </summary>
    /// <remarks>
    /// <para>可选能力端口：后端按需实现。"按组整体归档"的编排在上层
    /// （<c>ISessionGroupManager.ArchiveGroupAsync</c>），端口本身不感知组概念。</para>
    /// <para>未实现该端口的后端，上层归档调用将抛 <see cref="NotSupportedException"/>
    /// （能力显式缺失，不静默降级为删除）。</para>
    /// </remarks>
    public interface ISessionArchiveStore
    {
        /// <summary>归档单个会话：从活跃集移出、保留可恢复数据；返回是否实际归档（源不存在返回 false）。</summary>
        Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default);

        /// <summary>从归档恢复单个会话回活跃集；返回是否成功。</summary>
        Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default);

        /// <summary>枚举已归档会话摘要。</summary>
        IAsyncEnumerable<SessionSummary> ListArchivedAsync(CancellationToken ct = default);
    }
}
