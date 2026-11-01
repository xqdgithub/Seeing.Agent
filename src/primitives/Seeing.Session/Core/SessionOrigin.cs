namespace Seeing.Session.Core
{
    /// <summary>
    /// 会话来源/用途（与 <see cref="SessionKind"/> 正交）。
    /// <para><see cref="SessionKind"/> 表达结构（Root / SubAgent）且被组不变量结构性使用，
    /// 不能承载「谁创建的、为什么创建」这类用途语义；本枚举承担该职责。</para>
    /// <para>平台内部创建的会话（复盘 / 定时任务 / 心跳）应标记为对应来源，
    /// 以便视图层与用户发起的任务区分；存量会话缺字段时按 0 反序列化为 <see cref="User"/>。</para>
    /// </summary>
    public enum SessionOrigin
    {
        /// <summary>用户发起（默认）：用户在界面/CLI 中直接创建或驱动的会话。</summary>
        User = 0,

        /// <summary>子 Agent 任务：由 Task 工具为委派子任务创建的会话。</summary>
        SubAgentTask = 1,

        /// <summary>复盘（反思）：平台自动创建用于回顾与总结既有会话的会话。</summary>
        Reflection = 2,

        /// <summary>定时任务：由调度器按 cron 计划触发创建的会话。</summary>
        Scheduler = 3,

        /// <summary>心跳：由心跳触发器周期性创建的会话。</summary>
        Heartbeat = 4
    }
}
