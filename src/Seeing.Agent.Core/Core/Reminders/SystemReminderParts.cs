namespace Seeing.Agent.Core.Reminders;

public sealed record SystemReminderParts(
    string Source,
    string Kind,
    string Notice,
    string Task,
    string? TaskId = null,
    string Raw = "");
