namespace Seeing.Agent.Core.Reminders;

public static class SystemReminder
{
    public const string Tag = "system-reminder";

    public static class Sources
    {
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
    }

    public static class MetadataKeys
    {
        public const string Reminder = "reminder";
        public const string Source = "reminder_source";
        public const string Kind = "reminder_kind";
        public const string TaskId = "reminder_task_id";
    }
}
