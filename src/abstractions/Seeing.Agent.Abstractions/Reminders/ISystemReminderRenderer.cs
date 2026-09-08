using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Reminders;

/// <summary>
/// 系统提醒包装 / 解析。实现由 Core 提供；能力包仅依赖此接口。
/// </summary>
public interface ISystemReminderRenderer
{
    string Wrap(string taskBody, string source, string kind, string? taskId = null);

    bool TryParse(string content, out SystemReminderParts parts);

    SessionMessage CreateUserMessage(
        string taskBody, string source, string kind, string? taskId = null);

    /// <summary>将已包裹（或普通）文本转为 Session user 消息；若为 reminder 则附加 Metadata。</summary>
    SessionMessage ToUserMessage(string content);
}
