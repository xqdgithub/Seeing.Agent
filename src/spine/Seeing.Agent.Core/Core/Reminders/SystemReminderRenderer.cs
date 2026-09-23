using System.Text;
using System.Text.RegularExpressions;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Reminders;

/// <summary>
/// 系统提醒渲染器：将任务体包装为 system-reminder XML 信封，支持解析还原与转 Session 消息。
/// </summary>
public static partial class SystemReminderRenderer
{
    private const string EscapeClose = "<\\/";

    /// <summary>
    /// 将任务体与来源/类别包装为 system-reminder 信封文本（含转义）。
    /// </summary>
    public static string Wrap(string taskBody, string source, string kind, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source is required", nameof(source));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind is required", nameof(kind));

        taskBody ??= "";
        var notice = SystemReminderNotices.Resolve(source, kind);
        var escapedTask = EscapeTaskBody(taskBody);

        var sb = new StringBuilder();
        sb.Append('<').Append(SystemReminder.Tag);
        sb.Append(" source=\"").Append(source).Append('"');
        sb.Append(" kind=\"").Append(kind).Append('"');
        if (!string.IsNullOrEmpty(taskId))
            sb.Append(" task-id=\"").Append(taskId).Append('"');
        sb.AppendLine(">");
        sb.AppendLine("<notice>");
        sb.AppendLine(notice);
        sb.AppendLine("</notice>");
        sb.AppendLine("<task>");
        sb.AppendLine(escapedTask);
        sb.AppendLine("</task>");
        sb.Append("</").Append(SystemReminder.Tag).Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// 尝试从消息内容解析出提醒信封的各组成部分，失败返回 false。
    /// </summary>
    public static bool TryParse(string content, out SystemReminderParts parts)
    {
        parts = default!;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var match = ReminderRegex().Match(content);
        if (!match.Success)
            return false;

        var source = match.Groups["source"].Value;
        var kind = match.Groups["kind"].Value;
        var taskId = match.Groups["taskId"].Success ? match.Groups["taskId"].Value : null;
        var notice = match.Groups["notice"].Value.Trim('\r', '\n');
        var task = UnescapeTaskBody(match.Groups["task"].Value.Trim('\r', '\n'));

        parts = new SystemReminderParts(source, kind, notice, task, taskId, content);
        return true;
    }

    /// <summary>
    /// 构造携带提醒元数据的用户消息，用于注入会话历史。
    /// </summary>
    public static SessionMessage CreateUserMessage(
        string taskBody, string source, string kind, string? taskId = null)
    {
        var content = Wrap(taskBody, source, kind, taskId);
        return SessionMessage.UserMessage(content)
            .WithMetadata(SystemReminder.MetadataKeys.Reminder, true)
            .WithMetadata(SystemReminder.MetadataKeys.Source, source)
            .WithMetadata(SystemReminder.MetadataKeys.Kind, kind)
            .WithMetadata(SystemReminder.MetadataKeys.TaskId, taskId ?? "");
    }

    /// <summary>将已包裹（或普通）文本转为 Session user 消息；若为 reminder 则附加 Metadata。</summary>
    public static SessionMessage ToUserMessage(string content)
    {
        if (TryParse(content, out var parts))
        {
            var msg = SessionMessage.UserMessage(content)
                .WithMetadata(SystemReminder.MetadataKeys.Reminder, true)
                .WithMetadata(SystemReminder.MetadataKeys.Source, parts.Source)
                .WithMetadata(SystemReminder.MetadataKeys.Kind, parts.Kind);
            if (!string.IsNullOrEmpty(parts.TaskId))
                msg = msg.WithMetadata(SystemReminder.MetadataKeys.TaskId, parts.TaskId);
            return msg;
        }

        return SessionMessage.UserMessage(content);
    }

    private static string EscapeTaskBody(string body) =>
        body.Replace("</", EscapeClose, StringComparison.Ordinal);

    private static string UnescapeTaskBody(string body) =>
        body.Replace(EscapeClose, "</", StringComparison.Ordinal);

    // 允许属性顺序固定为 source、kind、可选 task-id（实现可更宽松）
    [GeneratedRegex(
        @"^<system-reminder\s+source=""(?<source>[^""]+)""\s+kind=""(?<kind>[^""]+)""(?:\s+task-id=""(?<taskId>[^""]+)"")?\s*>\s*<notice>\r?\n?(?<notice>.*?)\r?\n?</notice>\s*<task>\r?\n?(?<task>.*?)\r?\n?</task>\s*</system-reminder>\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ReminderRegex();
}
