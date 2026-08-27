using System.Text.Json;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;

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

        return viewModel;
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
        existing.Parameters = incoming.Parameters;
        existing.Result = incoming.Result;
        existing.Status = incoming.Status;
        existing.Error = incoming.Error;
        existing.TaskId = incoming.TaskId;
        existing.TaskAgent = incoming.TaskAgent;
        existing.TaskDescription = incoming.TaskDescription;
        existing.TaskBackground = incoming.TaskBackground;
        existing.TaskSteps = incoming.TaskSteps;
        existing.TodoList = incoming.TodoList;
        existing.IsExpanded = wasExpanded;
    }
}
