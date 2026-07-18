using System.Text.Json;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Session;

/// <summary>
/// 将执行流事件（Stream / ToolCall）投影到 Session 消息，供子会话落盘等无 UI 路径使用。
/// </summary>
public static class SessionStreamEventApplier
{
    /// <summary>
    /// 应用事件。返回 true 表示建议立即 Save。
    /// </summary>
    public static bool Apply(SessionData? session, IMessageEvent evt)
    {
        if (session == null)
            return false;

        switch (evt)
        {
            case StreamStartEvent start:
                ApplyStreamStart(session, start);
                return false;

            case StreamDeltaEvent delta:
                ApplyStreamDelta(session, delta);
                return false;

            case StreamCompleteEvent complete:
                return ApplyStreamComplete(session, complete);

            case ToolCallEvent tool:
                ApplyToolCall(session, tool);
                return tool.Status is ToolCallStatus.Success
                    or ToolCallStatus.Failed
                    or ToolCallStatus.Rejected
                    or ToolCallStatus.Pending;

            default:
                return false;
        }
    }

    private static void ApplyStreamStart(SessionData session, StreamStartEvent evt)
    {
        // 与 WebUI 一致：step>0（工具后下一轮）或尚无当前助手气泡时新建
        if (evt.Step > 0 || FindLastAssistant(session, evt.LoopId) == null)
        {
            var msg = SessionMessage.AssistantMessage(string.Empty);
            msg.Id = Guid.NewGuid().ToString("N")[..12];
            msg.LoopId = evt.LoopId;
            session.Messages.Add(msg);
        }
    }

    private static void ApplyStreamDelta(SessionData session, StreamDeltaEvent evt)
    {
        var msg = FindLastAssistant(session, evt.LoopId) ?? CreateAssistant(session, evt.LoopId);

        if (!string.IsNullOrEmpty(evt.LoopId))
            msg.LoopId = evt.LoopId;

        if (!string.IsNullOrEmpty(evt.ContentDelta))
            msg.Content += evt.ContentDelta;

        if (!string.IsNullOrEmpty(evt.ReasoningDelta))
        {
            msg.ReasoningContent =
                (msg.ReasoningContent ?? string.Empty) + evt.ReasoningDelta;
        }
    }

    private static bool ApplyStreamComplete(SessionData session, StreamCompleteEvent evt)
    {
        if (evt.Message == null ||
            string.Equals(evt.Message.Role, ChatRole.Tool, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var msg = FindLastAssistant(session, evt.LoopId) ?? CreateAssistant(session, evt.LoopId);

        if (!string.IsNullOrEmpty(evt.LoopId))
            msg.LoopId = evt.LoopId;

        // 子会话无 UI 缓冲：以完整 Message 为准补齐内容
        if (!string.IsNullOrEmpty(evt.Message.Content))
            msg.Content = evt.Message.Content;

        if (!string.IsNullOrEmpty(evt.Message.ReasoningContent))
            msg.ReasoningContent = evt.Message.ReasoningContent;

        if (evt.Message.ToolCalls is { Count: > 0 })
        {
            msg.ToolCalls ??= new List<SessionToolCall>();
            foreach (var tc in evt.Message.ToolCalls)
            {
                if (string.IsNullOrEmpty(tc.Id))
                    continue;

                var existing = msg.ToolCalls.Find(t => t.Id == tc.Id);
                if (existing == null)
                {
                    var args = tc.Function?.Arguments;
                    msg.ToolCalls.Add(new SessionToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Name,
                        Arguments = string.IsNullOrWhiteSpace(args) ? "{}" : args,
                        Status = "pending"
                    });
                }
                else
                {
                    if (string.IsNullOrEmpty(existing.Name) && !string.IsNullOrEmpty(tc.Name))
                        existing.Name = tc.Name;
                    if (string.IsNullOrEmpty(existing.Arguments) || existing.Arguments == "{}")
                    {
                        var args = tc.Function?.Arguments;
                        existing.Arguments = string.IsNullOrWhiteSpace(args) ? "{}" : args;
                    }
                }
            }
        }

        return true;
    }

    private static void ApplyToolCall(SessionData session, ToolCallEvent evt)
    {
        var msg = FindLastAssistant(session, evt.LoopId) ?? CreateAssistant(session, evt.LoopId);
        msg.ToolCalls ??= new List<SessionToolCall>();

        var toolCallId = evt.ToolCallId ?? Guid.NewGuid().ToString("N");
        var tc = msg.ToolCalls.Find(t => t.Id == toolCallId);
        if (tc == null)
        {
            tc = new SessionToolCall
            {
                Id = toolCallId,
                Name = evt.ToolName ?? string.Empty,
                Arguments = FormatArguments(evt.Arguments)
            };
            msg.ToolCalls.Add(tc);
        }
        else
        {
            if (string.IsNullOrEmpty(tc.Name) && !string.IsNullOrEmpty(evt.ToolName))
                tc.Name = evt.ToolName;
            if (string.IsNullOrEmpty(tc.Arguments) || tc.Arguments == "{}")
                tc.Arguments = FormatArguments(evt.Arguments);
        }

        tc.Status = evt.Status switch
        {
            ToolCallStatus.Pending => "pending",
            ToolCallStatus.Running => "running",
            ToolCallStatus.Success => "success",
            ToolCallStatus.Failed => "failed",
            ToolCallStatus.Rejected => "rejected",
            _ => tc.Status
        };

        if (!string.IsNullOrEmpty(evt.Output))
            tc.Result = evt.Output;
        if (!string.IsNullOrEmpty(evt.Error))
            tc.Error = evt.Error;
    }

    private static SessionMessage? FindLastAssistant(SessionData session, string? loopId)
    {
        return session.Messages
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(m => m.Role == "assistant"
                && (string.IsNullOrEmpty(loopId) || m.LoopId == loopId
                    || string.IsNullOrEmpty(m.LoopId)));
    }

    private static SessionMessage CreateAssistant(SessionData session, string? loopId)
    {
        var msg = SessionMessage.AssistantMessage(string.Empty);
        msg.Id = Guid.NewGuid().ToString("N")[..12];
        msg.LoopId = loopId;
        session.Messages.Add(msg);
        return msg;
    }

    private static string FormatArguments(object? arguments)
    {
        if (arguments == null)
            return "{}";
        if (arguments is string s)
            return string.IsNullOrWhiteSpace(s) ? "{}" : s;
        try
        {
            return JsonSerializer.Serialize(arguments);
        }
        catch
        {
            return "{}";
        }
    }
}
