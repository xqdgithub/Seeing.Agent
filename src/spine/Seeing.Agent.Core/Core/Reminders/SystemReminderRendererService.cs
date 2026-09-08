using Seeing.Agent.Abstractions.Reminders;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Reminders;

/// <summary>
/// <see cref="ISystemReminderRenderer"/> 默认实现 — 委托给静态 <see cref="SystemReminderRenderer"/>。
/// </summary>
public sealed class SystemReminderRendererService : ISystemReminderRenderer
{
    public string Wrap(string taskBody, string source, string kind, string? taskId = null) =>
        SystemReminderRenderer.Wrap(taskBody, source, kind, taskId);

    public bool TryParse(string content, out SystemReminderParts parts) =>
        SystemReminderRenderer.TryParse(content, out parts);

    public SessionMessage CreateUserMessage(
        string taskBody, string source, string kind, string? taskId = null) =>
        SystemReminderRenderer.CreateUserMessage(taskBody, source, kind, taskId);

    public SessionMessage ToUserMessage(string content) =>
        SystemReminderRenderer.ToUserMessage(content);
}
