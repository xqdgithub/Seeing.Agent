using Seeing.Agent.Abstractions.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话摘要卡片的预计算投影：把「全量消息扫描」从渲染期移到状态变更时，一次算好。
/// <para>
/// 背景：Summary 侧栏卡片原先在每次渲染时对全部消息跑 LINQ（最后一条 assistant、运行中工具、
/// 子代理计数）。组内 M 个成员时，一个组事件就会触发 M 次全表扫描。
/// </para>
/// <para>纯函数、无副作用，便于单测。</para>
/// </summary>
public static class SessionSummaryProjector
{
    /// <summary>运行中动作行的默认文案。</summary>
    private const string DefaultActionLine = "⚙ 正在执行";

    /// <summary>无消息时的预览文案。</summary>
    private const string EmptyPreview = "暂无消息";

    /// <summary>
    /// 构建摘要投影快照。
    /// </summary>
    /// <param name="session">会话快照（null 视为空会话）。</param>
    /// <param name="isExecuting">是否执行中（本地执行态）。</param>
    /// <param name="status">执行状态。</param>
    /// <param name="parentTitle">来源会话标题（用于来源行）。</param>
    /// <param name="relation">在组内的关系角色。</param>
    /// <param name="isAnchor">是否为锚点（无来源）。</param>
    /// <param name="successorTitle">交接后继标题（交接前任的来源行取它）。</param>
    public static SessionSummarySnapshot Build(
        SessionData? session,
        bool isExecuting,
        ExecutionStatus? status,
        string? parentTitle,
        SessionRelation relation,
        bool isAnchor,
        string? successorTitle)
    {
        var stateKind = SessionSummaryView.Classify(isExecuting, status);

        // 一次遍历同时得到「最后一条非摘要 assistant」与「运行中工具集合」，
        // 避免 Summary 每次渲染都跑多趟 LINQ。
        SessionMessage? lastAssistant = null;
        var runningTasks = new List<SessionToolCall>();
        string? firstRunningToolName = null;

        var messages = session?.Messages;
        if (messages is { Count: > 0 })
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];

                // 反向扫描：第一条命中的非摘要 assistant 即「最新」
                if (lastAssistant is null
                    && string.Equals(message.Role, MessageRole.Assistant, StringComparison.OrdinalIgnoreCase)
                    && !message.IsSummary)
                {
                    lastAssistant = message;
                }

                if (message.ToolCalls is not { Count: > 0 } toolCalls)
                    continue;

                foreach (var toolCall in toolCalls)
                {
                    if (!IsRunning(toolCall.Status))
                        continue;

                    if (string.Equals(toolCall.Name, "task", StringComparison.OrdinalIgnoreCase))
                    {
                        runningTasks.Add(toolCall);
                    }
                    else if (firstRunningToolName is null && !string.IsNullOrEmpty(toolCall.Name))
                    {
                        firstRunningToolName = toolCall.Name;
                    }
                }
            }
        }

        return new SessionSummarySnapshot(
            stateKind,
            BuildPreview(lastAssistant, firstRunningToolName, runningTasks),
            BuildActionLine(isExecuting, stateKind, firstRunningToolName, runningTasks),
            BuildSourceLine(parentTitle, relation, isAnchor, successorTitle),
            BuildStatusBarClass(isExecuting, status));
    }

    /// <summary>状态字符串是否表示「运行中」。</summary>
    private static bool IsRunning(string? status)
        => string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 预览正文：最新 assistant 文本 → 运行中工具名 → 占位文案。
    /// <para>与改造前的三档回退语义保持一致。</para>
    /// </summary>
    private static string BuildPreview(
        SessionMessage? lastAssistant,
        string? firstRunningToolName,
        IReadOnlyList<SessionToolCall> runningTasks)
    {
        if (lastAssistant is not null && !string.IsNullOrWhiteSpace(lastAssistant.Content))
            return SessionSummaryView.PreviewSnippet(lastAssistant.Content);

        var toolName = firstRunningToolName
            ?? runningTasks.FirstOrDefault(t => !string.IsNullOrEmpty(t.Name))?.Name;

        return toolName is not null
            ? $"正在执行工具: {toolName}"
            : EmptyPreview;
    }

    /// <summary>
    /// 动作行：仅执行中时展示。
    /// <para>task 工具按 TaskId 去重计数（推断不出子代理数时退化为通用文案）。</para>
    /// </summary>
    private static string BuildActionLine(
        bool isExecuting,
        SummaryState stateKind,
        string? firstRunningToolName,
        IReadOnlyList<SessionToolCall> runningTasks)
    {
        if (!isExecuting && stateKind != SummaryState.Running)
            return string.Empty;

        if (runningTasks.Count > 0)
        {
            var subagentCount = runningTasks
                .Where(t => !string.IsNullOrEmpty(t.TaskId))
                .Select(t => t.TaskId!)
                .Distinct(StringComparer.Ordinal)
                .Count();

            return subagentCount > 0
                ? $"{DefaultActionLine} task · {subagentCount} 个子代理"
                : $"{DefaultActionLine} task";
        }

        return firstRunningToolName is not null
            ? $"{DefaultActionLine} {firstRunningToolName}"
            : DefaultActionLine;
    }

    /// <summary>来源行：锚点无来源；交接前任取后继标题；其余取父会话标题。</summary>
    private static string BuildSourceLine(
        string? parentTitle,
        SessionRelation relation,
        bool isAnchor,
        string? successorTitle)
    {
        if (isAnchor)
            return string.Empty;

        var source = relation switch
        {
            SessionRelation.HandoffPredecessor => RelationshipSource.HandoffTo,
            SessionRelation.None => RelationshipSource.None,
            _ => RelationshipSource.Source
        };

        var otherTitle = source == RelationshipSource.HandoffTo ? successorTitle : parentTitle;
        return SessionSummaryView.SourceLine(source, otherTitle);
    }

    /// <summary>左侧状态条 CSS 类（颜色随代理状态）。</summary>
    private static string BuildStatusBarClass(bool isExecuting, ExecutionStatus? status)
    {
        if (isExecuting || status is ExecutionStatus.Running or ExecutionStatus.Pending)
            return "conference-tile--running";
        if (status == ExecutionStatus.Failed)
            return "conference-tile--failed";
        if (status == ExecutionStatus.Cancelled)
            return "conference-tile--cancelled";
        return "conference-tile--done";
    }
}

/// <summary>
/// 会话摘要卡片的预计算投影结果。
/// </summary>
/// <param name="StateKind">状态分层（决定渲染哪种卡片形态）。</param>
/// <param name="Preview">预览正文（已截断）。</param>
/// <param name="ActionLine">运行中动作行（非运行态为空串）。</param>
/// <param name="SourceLine">来源行（无来源为空串）。</param>
/// <param name="StatusBarClass">左侧状态条 CSS 类。</param>
public readonly record struct SessionSummarySnapshot(
    SummaryState StateKind,
    string Preview,
    string ActionLine,
    string SourceLine,
    string StatusBarClass);
