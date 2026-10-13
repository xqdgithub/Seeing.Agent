using System.Runtime.CompilerServices;
using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 会话存储扩展：为可选能力端口提供降级路径。
    /// </summary>
    public static class SessionStoreExtensions
    {
        /// <summary>
        /// 列出会话摘要：后端实现 <see cref="ISessionSummaryProvider"/> 时原生调用；
        /// 否则回退 <see cref="ISessionStore.ListAsync"/> 并流式投影（逐条投影、不整体物化）。
        /// </summary>
        public static async IAsyncEnumerable<SessionSummary> ListSummariesAsync(
            this ISessionStore store,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);

            if (store is ISessionSummaryProvider provider)
            {
                await foreach (var summary in provider.ListSummariesAsync(ct).ConfigureAwait(false))
                    yield return summary;
                yield break;
            }

            await foreach (var session in store.ListAsync(ct).ConfigureAwait(false))
                yield return SessionSummary.FromSession(session);
        }

        /// <summary>
        /// 后端是否支持归档能力。写回装饰器恒实现 <see cref="ISessionArchiveStore"/>，
        /// 故需递归按其内层能力判定（见 <see cref="IWriteBehindSessionStore.InnerSupportsArchive"/>）。
        /// </summary>
        public static bool SupportsArchive(this ISessionStore store)
        {
            ArgumentNullException.ThrowIfNull(store);

            if (store is IWriteBehindSessionStore writeBehind)
                return writeBehind.InnerSupportsArchive;

            return store is ISessionArchiveStore;
        }

        /// <summary>
        /// 归档单个会话；后端未实现 <see cref="ISessionArchiveStore"/> 时抛
        /// <see cref="NotSupportedException"/>（能力显式缺失，不静默降级为删除）。
        /// </summary>
        public static Task<bool> ArchiveAsync(this ISessionStore store, string sessionId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            return RequireArchiveStore(store).ArchiveAsync(sessionId, ct);
        }

        /// <summary>
        /// 从归档恢复单个会话；后端未实现 <see cref="ISessionArchiveStore"/> 时抛
        /// <see cref="NotSupportedException"/>。
        /// </summary>
        public static Task<bool> RestoreAsync(this ISessionStore store, string sessionId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            return RequireArchiveStore(store).RestoreAsync(sessionId, ct);
        }

        /// <summary>
        /// 列出已归档会话摘要；后端未实现 <see cref="ISessionArchiveStore"/> 时返回空序列。
        /// </summary>
        public static IAsyncEnumerable<SessionSummary> ListArchivedAsync(
            this ISessionStore store, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            return store is ISessionArchiveStore archiveStore
                ? archiveStore.ListArchivedAsync(ct)
                : EmptyAsync(ct);
        }

        private static ISessionArchiveStore RequireArchiveStore(ISessionStore store) =>
            store as ISessionArchiveStore
            ?? throw new NotSupportedException(
                $"存储后端 {store.GetType().Name} 未实现 {nameof(ISessionArchiveStore)}，不支持归档能力。");

        private static async IAsyncEnumerable<SessionSummary> EmptyAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
