namespace Seeing.Session.Core;

/// <summary>
/// 斜杠命令与它对应的那条 user 消息的定位规则（唯一实现）。
/// <para>
/// 为什么需要共享：命令执行时由宿主先把用户输入落盘为 user 消息，命令展开后要把
/// <b>命令原文所在的那条消息</b>改写成展开结果。新架构下用户消息之后还会插入项目指令等
/// 消息，盲取「最后一条 user 消息」会改错对象（命令原文残留并透传给模型）。
/// </para>
/// <para>匹配键是命令原文（<c>CommandContext.Input</c>），从尾部向前找最后一条完全相等的 user 消息。</para>
/// </summary>
public static class SessionCommandMessage
{
    /// <summary>是否存在与命令原文匹配的 user 消息。</summary>
    public static bool HasMatch(SessionData? session, string? input)
        => Find(session, input) is not null;

    /// <summary>把匹配到的命令 user 消息内容替换为 <paramref name="content"/>；未命中返回 false。</summary>
    public static bool Replace(SessionData? session, string? input, string content)
    {
        var message = Find(session, input);
        if (message is null)
            return false;

        message.Content = content;
        return true;
    }

    private static SessionMessage? Find(SessionData? session, string? input)
    {
        if (session is null || string.IsNullOrEmpty(input))
            return null;

        var messages = session.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (string.Equals(message.Role, MessageRole.User, StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Content, input, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return null;
    }
}
