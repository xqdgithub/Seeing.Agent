namespace Seeing.Agent.WebUI.Models.Timeline;

public enum TimelineItemKind
{
    User,
    AssistantTurn,
    System,
    Compaction,
    Reminder,
    ProjectInstructions,

    /// <summary>上下文管道的增量叙述（独立种类：来源可为任意上下文源，不并入项目指令）。</summary>
    ContextUpdate
}
