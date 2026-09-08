namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话关系类型：区分根会话、Fork 分支与子 Agent 任务会话。
    /// </summary>
    public enum SessionKind
    {
        /// <summary>用户主会话（列表默认展示）</summary>
        Root = 0,

        /// <summary>Fork 分支会话</summary>
        Fork = 1,

        /// <summary>子 Agent / Task 会话（Id 即 task_id）</summary>
        SubAgent = 2
    }
}
