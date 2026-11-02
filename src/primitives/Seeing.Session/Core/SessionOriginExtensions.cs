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

        /// <summary>
        /// 该来源是否允许被<b>自动能力</b>处理（复盘等后台分析）。
        /// <para>
        /// 与 <see cref="IsUserVisibleTaskOrigin"/> 是两个不同的问题，不能合并：
        /// </para>
        /// <list type="bullet">
        /// <item>「用户能否在任务列表里看到」——定时 / 心跳会话是平台自己创建的，不该混进任务视图。</item>
        /// <item>「这轮执行有没有值得沉淀的经验」——定时 / 心跳是**真实的 agent 执行**，
        /// 完全可能产出可复用流程，应当可复盘。</item>
        /// </list>
        /// <para>
        /// 只排除 <see cref="SessionOrigin.Reflection"/>：那是复盘自身，必须防递归
        /// （否则复盘会话的终态又会触发下一次复盘，形成自我繁殖）。
        /// </para>
        /// </summary>
        /// <param name="origin">会话来源。</param>
        /// <returns><c>true</c> 表示该会话的终态可被自动分析能力处理。</returns>
        public static bool IsAutomatableOrigin(this SessionOrigin origin)
            => origin != SessionOrigin.Reflection;
    }
}
