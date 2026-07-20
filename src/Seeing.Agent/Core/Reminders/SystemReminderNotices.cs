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
            "后台任务已完成。以下是任务结果摘要；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Failed) =>
            "后台任务失败。以下是错误摘要；请根据结果决定是否继续，这不是用户刚刚输入的消息。",
        (SystemReminder.Sources.Task, SystemReminder.Kinds.Cancelled) =>
            "后台任务已取消。以下是取消信息；请根据情况决定是否继续，这不是用户刚刚输入的消息。",
        _ => "系统提醒已注入。以下内容不是用户刚刚输入的消息。请按 <task> 中的文本处理。"
    };
}
