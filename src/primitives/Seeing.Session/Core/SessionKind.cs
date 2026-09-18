namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话类型：区分根会话与子 Agent 任务会话。
    /// <para>关系语义（分支/交接等）已迁至 <see cref="SessionGroup"/>。</para>
    /// </summary>
    public enum SessionKind
    {
        /// <summary>用户主会话（列表默认展示）</summary>
        Root = 0,

        /// <summary>子 Agent / Task 会话（Id 即 task_id）</summary>
        SubAgent = 2
    }
}
