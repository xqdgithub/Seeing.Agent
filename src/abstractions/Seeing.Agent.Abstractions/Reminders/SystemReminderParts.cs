namespace Seeing.Agent.Abstractions.Reminders;

/// <summary>解析后的系统提醒各段内容。</summary>
public sealed record SystemReminderParts(
    string Source,
    string Kind,
    string Notice,
    string Task,
    string? TaskId = null,
    string Raw = "");
