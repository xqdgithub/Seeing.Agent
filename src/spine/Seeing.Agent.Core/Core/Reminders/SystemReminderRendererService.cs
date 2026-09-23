using Seeing.Agent.Abstractions.Reminders;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Reminders;

/// <summary>
/// <see cref="ISystemReminderRenderer"/> 默认实现 — 委托给静态 <see cref="SystemReminderRenderer"/>。
/// </summary>
public sealed class SystemReminderRendererService : ISystemReminderRenderer
{
    /// <summary>
    /// 委托静态渲染器包装提醒信封文本。
    /// </summary>
    public string Wrap(string taskBody, string source, string kind, string? taskId = null) =>
        SystemReminderRenderer.Wrap(taskBody, source, kind, taskId);

    /// <summary>
    /// 委托静态渲染器解析提醒信封。
    /// </summary>
    public bool TryParse(string content, out SystemReminderParts parts) =>
        SystemReminderRenderer.TryParse(content, out parts);

    /// <summary>
    /// 委托静态渲染器构造带提醒元数据的用户消息。
    /// </summary>
    public SessionMessage CreateUserMessage(
        string taskBody, string source, string kind, string? taskId = null) =>
        SystemReminderRenderer.CreateUserMessage(taskBody, source, kind, taskId);

    /// <summary>
    /// 委托静态渲染器将内容转为用户消息（提醒内容自动附加元数据）。
    /// </summary>
    public SessionMessage ToUserMessage(string content) =>
        SystemReminderRenderer.ToUserMessage(content);
}
