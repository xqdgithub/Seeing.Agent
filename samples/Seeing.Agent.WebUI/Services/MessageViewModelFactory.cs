using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Services;

public static class MessageViewModelFactory
{
    public static MessageViewModel FromSessionMessage(SessionMessage msg, string sessionId, bool isComplete = true)
    {
        var viewModel = new MessageViewModel
        {
            Id = msg.Id ?? Guid.NewGuid().ToString("N")[..8],
            SessionId = msg.SessionId ?? sessionId,
            LoopId = msg.LoopId,
            Step = msg.Step,
            Role = msg.Role,
            Content = msg.Content,
            Reasoning = msg.ReasoningContent,
            Timestamp = msg.CreatedAt.ToLocalTime(),
            IsComplete = isComplete,
            IsReasoningComplete = DeriveIsReasoningComplete(msg, isComplete)
        };

        if (msg.Parts != null && msg.Parts.Count > 0)
        {
            viewModel.Parts = msg.Parts.Select(p => ContentPartViewModel.FromSessionPart(p)).ToList();
        }

        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
        {
            viewModel.ToolCalls = msg.ToolCalls
                .Select(tc => ToolCallViewModel.FromSessionToolCall(tc, sessionId))
                .ToList();
        }

        // 压缩摘要消息：优先读取新属性 IsSummary，兼容旧的 Metadata 标记
        viewModel.IsCompactionSummary = msg.IsSummary
            || msg.Metadata?.TryGetValue("is_compaction_summary", out _) == true;
        // IsCompacted 不在此设置：由时间线构建时按"摘要位置"推导（摘要之前 = 已压缩历史）

        // 入站来源（inbox.*）：仅入站消息带此元数据，用于来源徽章与视觉区分
        if (msg.Metadata is { } inboxMeta
            && inboxMeta.TryGetValue(InboxMetadataKeys.SenderKind, out var senderKindObj))
        {
            viewModel.InboxSenderKind = senderKindObj?.ToString();
            viewModel.InboxSenderId = GetMetaString(inboxMeta, InboxMetadataKeys.SenderId);
            viewModel.InboxSenderName = GetMetaString(inboxMeta, InboxMetadataKeys.SenderName);
            viewModel.InboxSource = GetMetaString(inboxMeta, InboxMetadataKeys.Source);
            viewModel.InboxKind = GetMetaString(inboxMeta, InboxMetadataKeys.Kind);
            viewModel.InboxPresentation = GetMetaString(inboxMeta, InboxMetadataKeys.Presentation);
        }

        // 待消费标记（排队中）：inbox.consumed 缺失或 false，且 presentation=user
        viewModel.IsPendingInbox = IsPendingInbox(msg);

        if (SystemReminderRenderer.TryParse(msg.Content ?? "", out var reminderParts))
        {
            viewModel.IsSystemReminder = true;
            viewModel.ReminderSource = reminderParts.Source;
            viewModel.ReminderKind = reminderParts.Kind;
            viewModel.ReminderTaskBody = reminderParts.Task;
            viewModel.ReminderRaw = reminderParts.Raw;
        }
        else if (ProjectInstructionsRenderer.TryParse(msg.Content ?? "", out var instructionParts))
        {
            viewModel.IsProjectInstructions = true;
            viewModel.InstructionReason = instructionParts.Reason;
            viewModel.InstructionCwd = instructionParts.Cwd;
            viewModel.InstructionPaths = instructionParts.Files.Select(file => file.Path).ToArray();
            viewModel.InstructionRaw = instructionParts.Raw;
        }
        else if (msg.Metadata?.TryGetValue(ProjectInstructions.MetadataKeys.ProjectInstructions, out _) == true)
        {
            viewModel.IsProjectInstructions = true;
            if (msg.Metadata.TryGetValue(ProjectInstructions.MetadataKeys.Reason, out var reasonObj))
                viewModel.InstructionReason = reasonObj?.ToString();
            if (msg.Metadata.TryGetValue(ProjectInstructions.MetadataKeys.Cwd, out var cwdObj))
                viewModel.InstructionCwd = cwdObj?.ToString();
            if (msg.Metadata.TryGetValue(ProjectInstructions.MetadataKeys.Paths, out var pathsObj)
                && pathsObj is string pathsJson)
            {
                try
                {
                    viewModel.InstructionPaths =
                        JsonSerializer.Deserialize<string[]>(pathsJson)
                        ?? Array.Empty<string>();
                }
                catch (JsonException)
                {
                    viewModel.InstructionPaths = Array.Empty<string>();
                }
            }

            viewModel.InstructionRaw = msg.Content ?? "";
        }
        else if (msg.Metadata?.ContainsKey(SessionMetadataKeys.ContextUpdateSources) == true
                 || ContextUpdateEnvelope.IsContextUpdate(msg.Content))
        {
            // 上下文管道的增量叙述（<context-update>）：同样折叠展示，避免 32k 指令正文裸奔
            viewModel.IsProjectInstructions = true;
            viewModel.InstructionReason = ProjectInstructions.Reasons.ContentChange;
            viewModel.InstructionPaths = Array.Empty<string>();
            viewModel.InstructionRaw = msg.Content ?? "";
        }

        return viewModel;
    }

    /// <summary>
    /// 是否为待消费（排队中）的入站 user 消息：
    /// <c>Role==user</c> 且 <c>inbox.presentation==user</c> 且 <c>inbox.consumed != true</c>。
    /// 系统 / 子会话 / assistant 呈现的消息不视为排队。
    /// </summary>
    private static bool IsPendingInbox(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.User, StringComparison.OrdinalIgnoreCase))
            return false;

        var metadata = msg.Metadata;
        if (metadata is null)
            return false;

        var presentation = GetMetaString(metadata, InboxMetadataKeys.Presentation);
        if (!string.Equals(presentation, "user", StringComparison.OrdinalIgnoreCase))
            return false;

        return !(GetMetaBool(metadata, InboxMetadataKeys.Consumed) == true);
    }

    /// <summary>
    /// 读取元数据布尔值（兼容 bool / JsonElement / 字符串）。
    /// </summary>
    private static bool? GetMetaBool(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement je when je.ValueKind is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False => je.GetBoolean(),
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 读取元数据字符串值（兼容 string / JsonElement）。
    /// </summary>
    private static string? GetMetaString(IReadOnlyDictionary<string, object> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };
    }

    /// <summary>
    /// Reasoning finishes when the model moves on to content/tools, not only when the whole turn ends.
    /// </summary>
    public static bool DeriveIsReasoningComplete(SessionMessage msg, bool isComplete)
    {
        if (string.IsNullOrEmpty(msg.ReasoningContent))
            return false;

        if (isComplete)
            return true;

        if (!string.IsNullOrEmpty(msg.Content))
            return true;

        if (msg.ToolCalls is { Count: > 0 })
            return true;

        return false;
    }

    public static void MergeToolCall(MessageViewModel target, ToolCallViewModel incoming)
    {
        var existing = target.ToolCalls.FirstOrDefault(t => t.Id == incoming.Id);
        if (existing == null)
        {
            target.ToolCalls.Add(incoming);
            return;
        }

        var wasExpanded = existing.IsExpanded;
        existing.Name = incoming.Name;
        existing.Description = incoming.Description ?? existing.Description;
        existing.Parameters = incoming.Parameters;
        existing.Result = incoming.Result;
        existing.Status = incoming.Status;
        existing.Error = incoming.Error;
        existing.Metadata = incoming.Metadata ?? existing.Metadata;
        existing.DurationMs = incoming.DurationMs ?? existing.DurationMs;
        existing.TaskId = incoming.TaskId;
        existing.TaskAgent = incoming.TaskAgent;
        existing.TaskDescription = incoming.TaskDescription;
        existing.TaskBackground = incoming.TaskBackground;
        existing.TaskSteps = incoming.TaskSteps;
        existing.TodoList = incoming.TodoList;
        existing.IsExpanded = wasExpanded;
    }
}
