using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Models.Timeline;

public sealed class TimelineItem
{
    public required string Key { get; init; }
    public required TimelineItemKind Kind { get; init; }
    public int Revision { get; private set; }
    public MessageViewModel? Message { get; internal set; }
    public LoopGroupViewModel? Turn { get; internal set; }

    public void Touch() => Revision++;

    public static string AssistantKey(string? loopId, string messageId)
        => string.IsNullOrEmpty(loopId) ? $"single-{messageId}" : loopId;
}
