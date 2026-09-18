namespace Seeing.Session.Storage
{
    /// <summary>
    /// 写回装饰器的显式持久化屏障端口。
    /// </summary>
    /// <remarks>
    /// <para><c>FlushAsync</c>：等待"调用时刻该 key 的最新版本（或更新版本）"落盘；受 <c>ct</c> 约束；
    /// 不可恢复失败时抛出（供显式屏障使用）。</para>
    /// <para><c>TryFlushAsync</c> / <c>TryFlushAllAsync</c>：有界等待，不因内层写失败而抛出，
    /// 失败/超时返回 false（供读路径使用，保证活性）。</para>
    /// </remarks>
    public interface IWriteBehindSessionStore
    {
        /// <summary>等待指定会话的最新版本落盘；不可恢复失败时抛出。</summary>
        Task FlushAsync(string sessionId, CancellationToken ct = default);

        /// <summary>有界等待指定会话落盘；超时或失败返回 false，不抛出。</summary>
        Task<bool> TryFlushAsync(string sessionId, TimeSpan timeout, CancellationToken ct = default);

        /// <summary>等待所有待写会话落盘；不可恢复失败时抛出。</summary>
        Task FlushAllAsync(CancellationToken ct = default);

        /// <summary>有界等待所有待写会话落盘；超时或失败返回 false，不抛出。</summary>
        Task<bool> TryFlushAllAsync(TimeSpan timeout, CancellationToken ct = default);
    }
}
