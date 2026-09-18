using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话元数据目录端口：跨分区列出会话元数据与统计信息。
    /// </summary>
    /// <remarks>
    /// <para>由 <see cref="GlobalSessionStore"/> 实现；<c>SessionManager</c> 依赖本接口而非具体类型，
    /// 以便未来替换为 DB 目录实现。</para>
    /// <para>本次不注册到 DI，保持 <c>SessionManager</c> 的降级路径（内存缓存）行为不变。</para>
    /// </remarks>
    public interface ISessionCatalog
    {
        /// <summary>列出所有会话元数据（<paramref name="partitionId"/> 为 null 时跨分区）。</summary>
        Task<IReadOnlyList<SessionMetadata>> ListAllAsync(string? partitionId, CancellationToken ct = default);

        /// <summary>获取会话统计信息。</summary>
        Task<SessionStatistics> GetStatisticsAsync(CancellationToken ct = default);
    }
}
