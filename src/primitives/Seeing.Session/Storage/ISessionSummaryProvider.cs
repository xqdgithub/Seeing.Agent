using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话摘要端口：提供不含消息体的轻量列表，用于会话列表 / 归档列表展示。
    /// </summary>
    /// <remarks>
    /// <para>可选能力端口：后端按需实现。未实现时由
    /// <see cref="SessionStoreExtensions.ListSummariesAsync(ISessionStore, CancellationToken)"/>
    /// 降级为流式投影 <see cref="ISessionStore.ListAsync"/>（逐条投影、不整体物化）。</para>
    /// </remarks>
    public interface ISessionSummaryProvider
    {
        /// <summary>枚举全部活跃会话摘要（不含消息体）。</summary>
        IAsyncEnumerable<SessionSummary> ListSummariesAsync(CancellationToken ct = default);
    }
}
