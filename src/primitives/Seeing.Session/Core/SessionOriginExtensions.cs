namespace Seeing.Session.Core
{
    /// <summary>
    /// <see cref="SessionOrigin"/> 的语义谓词。
    /// <para>
    /// 把「哪些来源算用户可感知的任务视图成员」收敛到一处，避免
    /// <c>TaskTool</c> / <c>TaskStatusTool</c> / <c>TuiSurfaceProvider</c> 各写一遍枚举判断
    /// 而慢慢漂移。
    /// </para>
    /// </summary>
    public static class SessionOriginExtensions
    {
        /// <summary>
        /// 该来源是否应出现在用户可见的任务视图中（任务列表、子代理摘要、TUI 订阅组）。
        /// <para>
        /// <b>刻意用排除而非白名单</b>：<see cref="SessionOrigin"/> 字段是后加的，
        /// 升级前的历史子会话落盘时没有该字段，反序列化后为默认值
        /// <see cref="SessionOrigin.User"/>。若写成「必须是 SubAgentTask」，
        /// 所有存量 task 会话会在升级后集体从任务视图消失，且用户无法 resume。
        /// </para>
        /// <para>
        /// 因此这里只排除<b>平台内部来源</b>（复盘 / 定时 / 心跳）——它们一定是新代码创建的，
        /// 必然带正确标记，不存在历史兼容问题。
        /// </para>
        /// </summary>
        /// <param name="origin">会话来源。</param>
        /// <returns><c>true</c> 表示用户在任务视图中应当看到该会话。</returns>
        public static bool IsUserVisibleTaskOrigin(this SessionOrigin origin)
            => origin is SessionOrigin.User or SessionOrigin.SubAgentTask;
    }
}
