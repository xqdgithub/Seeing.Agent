namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话活动追踪：记录每个会话最后一次活动时刻，用于判定「空闲会话」。
    /// <para>
    /// 消费方是后台工作者（记忆抽取、复盘触发等）：它们按惰性时间窗扫描空闲会话，
    /// 而不是在每条消息上同步触发昂贵操作。
    /// </para>
    /// <para>
    /// 实现必须是<b>并发安全且无阻塞</b>的：<see cref="Touch"/> 会在每次执行的收尾路径上调用，
    /// 不得引入锁竞争或磁盘 I/O。
    /// </para>
    /// </summary>
    public interface ISessionActivityTracker
    {
        /// <summary>记录会话刚刚发生活动（覆盖式更新）。空/空白会话 ID 直接忽略。</summary>
        /// <param name="sessionId">会话 ID。</param>
        void Touch(string sessionId);

        /// <summary>
        /// 返回空闲时间已达 <paramref name="idle"/> 的所有会话 ID。
        /// <para>返回顺序不保证；调用方不应依赖排序。从未 <see cref="Touch"/> 过的会话不会出现。</para>
        /// </summary>
        /// <param name="idle">空闲判定阈值。</param>
        IReadOnlyList<string> GetIdleSessions(TimeSpan idle);

        /// <summary>移除会话的活动记录（会话被删除/清理时调用，避免字典无限增长）。</summary>
        /// <param name="sessionId">会话 ID。</param>
        void Clear(string sessionId);

        /// <summary>
        /// 只读查询会话最后一次活动时刻，<b>不刷新时间戳</b>。
        /// <para>
        /// 供后台工作者做二次校验：<see cref="GetIdleSessions"/> 返回的是快照，
        /// 从扫描到真正执行之间用户可能已经回到该会话。消费方据此确认「现在仍然空闲」。
        /// </para>
        /// </summary>
        /// <param name="sessionId">会话 ID。</param>
        /// <param name="lastActivity">最后一次活动时刻；会话不存在时为 <c>default</c>。</param>
        /// <returns>会话是否存在活动记录。</returns>
        bool TryGetLastActivity(string sessionId, out DateTimeOffset lastActivity);
    }
}
