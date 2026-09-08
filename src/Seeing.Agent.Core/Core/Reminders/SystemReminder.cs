namespace Seeing.Agent.Core.Reminders;

public static class SystemReminder
{
    public const string Tag = "system-reminder";

    public static class Sources
    {
        public const string Agent = "agent";
        public const string Job = "job";
        public const string Task = "task";
    }

    public static class Kinds
    {
        public const string Cron = "cron";
        public const string Heartbeat = "heartbeat";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string TodoEmpty = "todo_empty";
        public const string TodoIncomplete = "todo_incomplete";
        public const string TodoPaused = "todo_paused";
    }

    public static class MetadataKeys
    {
        public const string Reminder = "reminder";
        public const string Source = "reminder_source";
        public const string Kind = "reminder_kind";
        public const string TaskId = "reminder_task_id";
    }
}
