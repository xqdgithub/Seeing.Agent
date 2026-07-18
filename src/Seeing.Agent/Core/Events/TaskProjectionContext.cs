namespace Seeing.Agent.Core.Events;

/// <summary>子任务事件投影上下文</summary>
public sealed class TaskProjectionContext
{
    public TaskProjectionContext(
        string parentSessionId,
        string taskId,
        string? originToolCallId,
        bool background,
        string agentName,
        string description)
    {
        ParentSessionId = parentSessionId;
        TaskId = taskId;
        OriginToolCallId = originToolCallId;
        Background = background;
        AgentName = agentName;
        Description = description;
    }

    public string ParentSessionId { get; }
    public string TaskId { get; }
    public string? OriginToolCallId { get; }
    public bool Background { get; }
    public string AgentName { get; }
    public string Description { get; }
}
