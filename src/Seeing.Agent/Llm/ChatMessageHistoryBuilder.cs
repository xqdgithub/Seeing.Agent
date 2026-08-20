using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Llm;

/// <summary>
/// 会话消息 → LLM 消息历史转换器
/// <para>统一角色映射、内容提取与过滤规则，供标题生成（SessionTitleService）、会话压缩（LlmSummarizer）等场景复用。</para>
/// </summary>
public static class ChatMessageHistoryBuilder
{
    /// <summary>会话消息角色 → LLM 消息角色（未知角色归为 user）</summary>
    public static string MapRole(string role)
        => role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.Assistant => ChatRole.Assistant,
            MessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User
        };

    /// <summary>提取消息文本内容：Content 优先，回退 Parts 文本连接</summary>
    public static string ExtractTextContent(SessionMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content))
            return message.Content;

        if (message.Parts is { Count: > 0 })
        {
            var parts = message.Parts
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => p.Text!);
            return string.Join("\n", parts);
        }

        return string.Empty;
    }

    /// <summary>
    /// 构建 LLM 消息历史
    /// </summary>
    /// <param name="messages">会话消息</param>
    /// <param name="skipToolMessages">跳过工具消息（标题生成等场景）</param>
    /// <param name="skipEmptyContent">跳过无内容消息</param>
    /// <param name="mergeConsecutiveRoles">合并连续同角色消息（避免 Provider 校验失败）</param>
    public static List<ChatMessage> BuildHistory(
        IEnumerable<SessionMessage> messages,
        bool skipToolMessages = false,
        bool skipEmptyContent = false,
        bool mergeConsecutiveRoles = false)
    {
        var history = new List<ChatMessage>();

        foreach (var msg in messages)
        {
            if (skipToolMessages &&
                (msg.Role.Equals(ChatRole.Tool, StringComparison.OrdinalIgnoreCase) ||
                 msg.Role.Equals(MessageRole.Tool, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var content = ExtractTextContent(msg);
            if (skipEmptyContent && string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var role = MapRole(msg.Role);
            if (mergeConsecutiveRoles && history.Count > 0 &&
                history[^1].Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            {
                history[^1].Content = $"{history[^1].Content}\n{content}";
                continue;
            }

            history.Add(new ChatMessage { Role = role, Content = content });
        }

        return history;
    }
}