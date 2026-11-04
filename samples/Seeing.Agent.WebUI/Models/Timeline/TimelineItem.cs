namespace Seeing.Agent.WebUI.Models.Timeline;

public sealed class TimelineItem
{
    public required string Key { get; init; }
    public required TimelineItemKind Kind { get; init; }

    /// <summary>内容版本：任何可见变化都递增（驱动 TimelineItemView 重渲染）。</summary>
    public int Revision { get; private set; }

    /// <summary>结构版本：仅轮次内消息增删/重排时递增（驱动 LoopGroupItem 重建全部分段）。</summary>
    public int StructureRevision { get; private set; }

    public MessageViewModel? Message { get; internal set; }
    public LoopGroupViewModel? Turn { get; internal set; }

    /// <summary>标记内容变化（轮次结构不变）。</summary>
    public void Touch() => Revision++;

    /// <summary>标记结构变化（轮次内消息增删/重排），同时计入内容版本。</summary>
    public void TouchStructure()
    {
        Revision++;
        StructureRevision++;
    }

    public static string AssistantKey(string? loopId, string messageId)
        => string.IsNullOrEmpty(loopId) ? $"single-{messageId}" : loopId;
}
