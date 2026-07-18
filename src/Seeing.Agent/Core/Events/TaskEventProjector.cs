namespace Seeing.Agent.Core.Events;

/// <summary>
/// 默认投影器：工具事件 → TaskProgress；不镜像 StreamDelta 全文。
/// </summary>
public sealed class TaskEventProjector : ITaskEventProjector
{
    public const int MaxPreviewLength = 200;

    public IEnumerable<IMessageEvent> Project(IMessageEvent childEvent, TaskProjectionContext ctx)
    {
        if (childEvent is ToolCallEvent tool)
        {
            var stepKind = tool.Type switch
            {
                MessageEventType.ToolCallPending => "tool_pending",
                MessageEventType.ToolCallRunning => "tool_running",
                MessageEventType.ToolCallComplete => "tool_complete",
                _ => "tool_running"
            };

            yield return new TaskProgressEvent
            {
                SessionId = ctx.ParentSessionId,
                LoopId = childEvent.LoopId,
                TaskId = ctx.TaskId,
                ParentSessionId = ctx.ParentSessionId,
                OriginToolCallId = ctx.OriginToolCallId,
                ToolCallId = tool.ToolCallId,
                StepKind = stepKind,
                ToolName = tool.ToolName,
                Status = tool.Status.ToString(),
                Preview = Truncate(tool.Output ?? tool.Error)
            };
            yield break;
        }

        // StreamDelta 默认不镜像进父总线
        yield break;
    }

    public TaskStartedEvent CreateStarted(TaskProjectionContext ctx, string? loopId = null) => new()
    {
        SessionId = ctx.ParentSessionId,
        LoopId = loopId,
        TaskId = ctx.TaskId,
        ParentSessionId = ctx.ParentSessionId,
        OriginToolCallId = ctx.OriginToolCallId,
        AgentName = ctx.AgentName,
        Description = ctx.Description,
        Background = ctx.Background
    };

    public TaskCompletedEvent CreateCompleted(
        TaskProjectionContext ctx,
        string? resultText,
        TimeSpan? duration = null,
        string? loopId = null) => new()
    {
        SessionId = ctx.ParentSessionId,
        LoopId = loopId,
        TaskId = ctx.TaskId,
        ParentSessionId = ctx.ParentSessionId,
        OriginToolCallId = ctx.OriginToolCallId,
        ResultText = resultText,
        Duration = duration
    };

    public TaskFailedEvent CreateFailed(
        TaskProjectionContext ctx,
        string error,
        bool cancelled = false,
        string? loopId = null) => new()
    {
        SessionId = ctx.ParentSessionId,
        LoopId = loopId,
        TaskId = ctx.TaskId,
        ParentSessionId = ctx.ParentSessionId,
        OriginToolCallId = ctx.OriginToolCallId,
        Error = error,
        Cancelled = cancelled
    };

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        return text.Length <= MaxPreviewLength
            ? text
            : text[..MaxPreviewLength];
    }
}
