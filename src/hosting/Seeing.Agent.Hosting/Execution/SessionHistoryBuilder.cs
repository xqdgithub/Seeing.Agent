using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Llm;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话消息 → LLM 聊天历史的唯一重建实现。
/// <para>
/// <b>为什么集中：</b>历史重建有两类硬约束，一旦散落就会各自漂移：
/// </para>
/// <list type="number">
/// <item><b>只取活跃消息</b>（<see cref="SessionData.GetActiveMessages"/>）：已压缩标记的旧消息保留展示
/// 但不传给 LLM，否则模型会看到被摘要取代的陈旧细节。</item>
/// <item><b>工具结果必须展开为独立 tool 消息</b>：会话里工具结果内嵌于 assistant 消息的
/// <c>ToolCalls[].Result</c>，而 OpenAI 要求 <c>assistant(tool_calls)</c> 后必须紧跟对应的 tool 消息，
/// 否则重建历史会直接触发 400。</item>
/// </list>
/// <para>
/// 另有三类消息<b>可持久化但不进 LLM 历史</b>：旧版仅承载 schema_snapshot 的空 system 载体、
/// 运行时通知（错误/取消，新版已不再产生，此处兼容存量）、
/// 以及 <c>inbox.presentation=none</c> 的入站通知（如 Gateway 横幅）。
/// </para>
/// </summary>
internal static class SessionHistoryBuilder
{
    /// <summary>
    /// 从会话重建 LLM 聊天历史。
    /// </summary>
    /// <param name="session">会话数据（读取活跃消息）。</param>
    /// <param name="imageParts">
    /// 内容段物化器（把 <c>LocalPath</c> 读成 base64）。null 表示不物化——仅测试/无图路径使用。
    /// <para><b>可选参数</b>：本方法被十余处测试经门面直接调用，加必填参数会全部编译失败。</para>
    /// </param>
    public static List<ChatMessage> BuildHistory(SessionData session, IImagePartResolver? imageParts = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var history = new List<ChatMessage>();

        // 统一消息来源：仅活跃消息（已压缩标记的旧消息保留展示但不传递给 LLM）
        foreach (var msg in session.GetActiveMessages())
        {
            if (IsSchemaSnapshotCarrierMessage(msg))
                continue;

            if (IsRuntimeNotificationMessage(msg))
                continue;

            if (IsNonHistoryInboxMessage(msg))
                continue;

            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent,
                ReasoningSignature = msg.ReasoningSignature,
                ToolCallId = msg.ToolCallId
            };

            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                chatMessage.Parts = msg.Parts
                    .Select(p => ToChatPart(session.Id, p, imageParts))
                    .ToList();
            }

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                chatMessage.ToolCalls = msg.ToolCalls.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new FunctionCall
                    {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }).ToList();
            }

            history.Add(chatMessage);

            // 工具结果在会话中内嵌于 assistant 消息的 ToolCalls[].Result，
            // 此处展开为独立的 tool 消息，满足 OpenAI 对 assistant(tool_calls)
            // 后必须紧跟对应 tool 消息的要求，否则重建历史会触发 400。
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = ToolResultFormatting.ToModelContent(
                            string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase),
                            tc.Result,
                            tc.Error,
                            tc.Name,
                            tc.Status,
                            tc.Title,
                            tc.ToolDefinition),
                        // 工具产出的图片经物化后挂在 tool 消息的 Parts 上；
                        // 出站形状（内嵌 / 拆消息 / 聚合）由各 client 决定。
                        Parts = tc.Parts is { Count: > 0 }
                            ? tc.Parts.Select(p => ToChatPart(session.Id, p, imageParts)).ToList()
                            : null
                    });
                }
            }
        }

        return history;
    }

    /// <summary>
    /// 会话内容段 → LLM 内容段。
    /// <para>
    /// 有 <c>LocalPath</c> 且接入物化器时读盘成 base64；否则按内联字段直传。
    /// 同时补齐历史遗漏的 <c>FileId</c> / <c>ImageDetail</c>（既有 bug）。
    /// </para>
    /// </summary>
    private static ChatContentPart ToChatPart(
        string sessionId,
        SessionContentPart part,
        IImagePartResolver? imageParts)
    {
        if (imageParts is not null && !string.IsNullOrEmpty(part.LocalPath))
            return imageParts.Resolve(sessionId, part);

        return new ChatContentPart
        {
            Type = part.Type,
            Text = part.Text,
            Url = part.Url,
            DataBase64 = part.DataBase64,
            MimeType = part.MimeType,
            FileName = part.FileName,
            FileId = part.FileId,
            ImageDetail = part.ImageDetail
        };
    }

    /// <summary>
    /// 旧版空 system 仅承载 schema_snapshot，不进入 LLM 历史。
    /// </summary>
    private static bool IsSchemaSnapshotCarrierMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(msg.Content))
            return false;
        if (msg.Parts is { Count: > 0 })
            return false;
        return msg.Metadata?.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey) == true;
    }

    /// <summary>
    /// 运行时通知（错误/取消）不进 LLM 历史。
    /// 新数据已不再产生；此处兼容存量消息（前缀）与显式 transient 标记。
    /// </summary>
    private static bool IsRuntimeNotificationMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;

        if (msg.Metadata?.TryGetValue("transient", out var flag) == true)
        {
            if (flag is bool b && b)
                return true;
            // 落盘再加载后 Metadata 值为 JsonElement，需兼容
            if (flag is JsonElement je && je.ValueKind == JsonValueKind.True)
                return true;
        }

        if (string.IsNullOrEmpty(msg.Content))
            return false;

        return msg.Content.StartsWith("错误: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("对话已取消: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("压缩失败", StringComparison.Ordinal)
            || msg.Content.StartsWith("⚠️ 执行已取消", StringComparison.Ordinal)
            || msg.Content.StartsWith("❌ 执行出错", StringComparison.Ordinal);
    }

    /// <summary>
    /// 入站消息中 <c>inbox.presentation=none</c>（可持久化展示但不进 LLM 历史，如 Gateway 横幅）。
    /// 落盘再加载后 Metadata 值可能为 <see cref="JsonElement"/>，故用 <c>ToString()</c> 兼容。
    /// </summary>
    private static bool IsNonHistoryInboxMessage(SessionMessage msg)
    {
        if (msg.Metadata?.TryGetValue(InboxMetadataKeys.Presentation, out var raw) != true || raw is null)
            return false;

        var presentation = raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };

        return string.Equals(presentation, "none", StringComparison.OrdinalIgnoreCase);
    }
}
