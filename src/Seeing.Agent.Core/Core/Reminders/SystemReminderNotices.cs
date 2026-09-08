namespace Seeing.Agent.Core.Reminders;

public static class SystemReminderNotices
{
    public static string Resolve(string source, string kind) => (source, kind) switch
    {
        (SystemReminder.Sources.Job, SystemReminder.Kinds.Cron) =>
            "定时任务已触发。以下内容是系统注入的指令，不是用户输入的消息。请按 <task> 中的文本执行。",
        (SystemReminder.Sources.Job, SystemReminder.Kinds.Heartbeat) =>
            "心跳任务已触发。以下内容是系统注入的指令，不是用户输入的消息。请按 <task> 中的文本执行。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Completed) =>
            "后台任务已完成。以下是任务结果；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Failed) =>
            "后台任务失败。以下是错误信息；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Cancelled) =>
            "后台任务已取消。以下是取消信息；请根据情况决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoEmpty) =>
            "你已执行多步操作但尚未创建 todo 列表。如果当前任务涉及 2 个或以上独立步骤，请使用 TodoWrite 工具规划。",
        (SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoIncomplete) =>
            "你有未完成的 todo 任务。必须将所有 todo 标记为 completed、cancelled 或 paused 后才能结束。",
        (SystemReminder.Sources.Agent, SystemReminder.Kinds.TodoPaused) =>
            "你有处于暂停状态的任务。请检查并恢复需要继续的任务。",
        _ => "系统提醒已注入。以下内容不是用户刚刚输入的消息。请按 <task> 中的文本处理。"
    };
}
