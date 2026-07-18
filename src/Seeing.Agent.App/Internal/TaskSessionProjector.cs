using System.Text.Json;
using Seeing.Agent.Core.Events;
using Seeing.Session.Core;

namespace Seeing.Agent.App.Internal;

/// <summary>
/// 将 Task / ToolCall 事件投影到 Session 消息上的 ToolCalls（含 task_* 字段），保证落盘完整。
/// </summary>
public static class TaskSessionProjector
{
    public static void Apply(SessionData? session, IMessageEvent evt)
    {
        if (session == null)
            return;

        switch (evt)
        {
            case ToolCallEvent tool:
                ApplyToolCall(session, tool);
                break;
            case TaskStartedEvent started:
                ApplyTaskStarted(session, started);
                break;
            case TaskProgressEvent progress:
                ApplyTaskProgress(session, progress);
                break;
            case TaskCompletedEvent completed:
                ApplyTaskCompleted(session, completed);
                break;
            case TaskFailedEvent failed:
                ApplyTaskFailed(session, failed);
                break;
        }
    }

    private static void ApplyToolCall(SessionData session, ToolCallEvent evt)
    {
        var msg = FindOrCreateAssistantMessage(session, evt.LoopId);
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

        EnrichFromPayload(tc);
    }

    private static void ApplyTaskStarted(SessionData session, TaskStartedEvent evt)
    {
        var tc = ResolveOrAttach(session, evt.OriginToolCallId, evt.TaskId, evt.LoopId);
        tc.Name = "task";
        tc.TaskId = evt.TaskId;
        tc.TaskAgent = evt.AgentName;
        tc.TaskDescription = evt.Description;
        tc.TaskBackground = evt.Background;
        tc.TaskSteps ??= new List<SessionTaskStep>();
        if (string.Equals(tc.Status, "pending", StringComparison.OrdinalIgnoreCase))
            tc.Status = "running";
        EnrichFromPayload(tc);
    }

    private static void ApplyTaskProgress(SessionData session, TaskProgressEvent evt)
    {
        var tc = ResolveOrAttach(session, evt.OriginToolCallId, evt.TaskId, evt.LoopId);
        tc.TaskId ??= evt.TaskId;
        tc.Name = "task";
        tc.TaskSteps ??= new List<SessionTaskStep>();

        var existing = FindStepToUpdate(tc.TaskSteps, evt);
        if (existing != null)
        {
            existing.StepKind = evt.StepKind;
            existing.Status = evt.Status;
            existing.Timestamp = evt.Timestamp;
            if (!string.IsNullOrEmpty(evt.ToolCallId))
                existing.ToolCallId = evt.ToolCallId;
            if (!string.IsNullOrEmpty(evt.ToolName))
                existing.ToolName = evt.ToolName;
            // Pending/Running 通常无 Preview；完成时写入，勿被空值清掉
            if (!string.IsNullOrEmpty(evt.Preview))
                existing.Preview = evt.Preview;
            return;
        }

        tc.TaskSteps.Add(new SessionTaskStep
        {
            StepKind = evt.StepKind,
            ToolCallId = evt.ToolCallId,
            ToolName = evt.ToolName,
            Status = evt.Status,
            Preview = evt.Preview,
            Timestamp = evt.Timestamp
        });
    }

    private static SessionTaskStep? FindStepToUpdate(List<SessionTaskStep> steps, TaskProgressEvent evt)
    {
        if (!string.IsNullOrEmpty(evt.ToolCallId))
        {
            var byId = steps.Find(s =>
                string.Equals(s.ToolCallId, evt.ToolCallId, StringComparison.Ordinal));
            if (byId != null)
                return byId;
        }

        if (string.IsNullOrEmpty(evt.ToolName))
            return null;

        // 无 ToolCallId 时：同名工具的最后一条若仍在进行中，视为状态更新
        return steps.AsEnumerable().Reverse()
            .FirstOrDefault(s =>
                string.Equals(s.ToolName, evt.ToolName, StringComparison.OrdinalIgnoreCase)
                && IsInFlightStatus(s.Status));
    }

    private static bool IsInFlightStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return false;
        return status.Equals("Pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Running", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyTaskCompleted(SessionData session, TaskCompletedEvent evt)
    {
        var tc = ResolveOrAttach(session, evt.OriginToolCallId, evt.TaskId, evt.LoopId);
        tc.Name = "task";
        tc.TaskId ??= evt.TaskId;
        if (!string.IsNullOrEmpty(evt.ResultText))
            tc.Result = evt.ResultText;
        tc.Status = "success";
        EnrichFromPayload(tc);
    }

    private static void ApplyTaskFailed(SessionData session, TaskFailedEvent evt)
    {
        var tc = ResolveOrAttach(session, evt.OriginToolCallId, evt.TaskId, evt.LoopId);
        tc.Name = "task";
        tc.TaskId ??= evt.TaskId;
        tc.Error = evt.Error;
        tc.Status = evt.Cancelled ? "cancelled" : "failed";
        EnrichFromPayload(tc);
    }

    private static SessionToolCall ResolveOrAttach(
        SessionData session,
        string? originToolCallId,
        string taskId,
        string? loopId)
    {
        foreach (var msg in session.Messages.AsEnumerable().Reverse())
        {
            if (msg.ToolCalls == null)
                continue;

            if (!string.IsNullOrEmpty(originToolCallId))
            {
                var byOrigin = msg.ToolCalls.Find(t => t.Id == originToolCallId);
                if (byOrigin != null)
                    return byOrigin;
            }

            var byTask = msg.ToolCalls.Find(t => t.TaskId == taskId);
            if (byTask != null)
                return byTask;
        }

        var host = FindOrCreateAssistantMessage(session, loopId);
        host.ToolCalls ??= new List<SessionToolCall>();
        var created = new SessionToolCall
        {
            Id = originToolCallId ?? $"task_{taskId}",
            Name = "task",
            Status = "running",
            TaskId = taskId
        };
        host.ToolCalls.Add(created);
        return created;
    }

    private static SessionMessage FindOrCreateAssistantMessage(SessionData session, string? loopId)
    {
        var existing = session.Messages
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(m => m.Role == "assistant"
                && (string.IsNullOrEmpty(loopId) || m.LoopId == loopId));

        if (existing != null)
            return existing;

        var msg = SessionMessage.AssistantMessage(string.Empty);
        msg.Id = Guid.NewGuid().ToString("N")[..12];
        msg.LoopId = loopId;
        session.Messages.Add(msg);
        return msg;
    }

    private static void EnrichFromPayload(SessionToolCall tc)
    {
        if (!string.IsNullOrEmpty(tc.TaskId)
            || string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(tc.Result) && tc.Result.Contains("task_id:", StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(tc.Arguments) && tc.Arguments.Contains("subagent_type", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(tc.Name))
                tc.Name = "task";
        }

        if (string.IsNullOrEmpty(tc.TaskId) && !string.IsNullOrEmpty(tc.Result))
        {
            const string prefix = "task_id:";
            var idx = tc.Result.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + prefix.Length;
                while (start < tc.Result.Length && char.IsWhiteSpace(tc.Result[start]))
                    start++;
                var end = start;
                while (end < tc.Result.Length && !char.IsWhiteSpace(tc.Result[end]))
                    end++;
                if (end > start)
                    tc.TaskId = tc.Result[start..end];
            }
        }

        if (string.IsNullOrWhiteSpace(tc.Arguments))
            return;

        try
        {
            using var doc = JsonDocument.Parse(tc.Arguments);
            var root = doc.RootElement;
            if (string.IsNullOrEmpty(tc.TaskDescription) &&
                root.TryGetProperty("description", out var desc) &&
                desc.ValueKind == JsonValueKind.String)
            {
                tc.TaskDescription = desc.GetString();
            }

            if (string.IsNullOrEmpty(tc.TaskAgent) &&
                root.TryGetProperty("subagent_type", out var agent) &&
                agent.ValueKind == JsonValueKind.String)
            {
                tc.TaskAgent = agent.GetString();
            }

            if (root.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True)
                tc.TaskBackground = true;
        }
        catch
        {
            // ignore
        }
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
