namespace Seeing.Agent.Abstractions.Llm;

public enum ModelCapabilitiesChangeReason
{
    SystemConfigChanged,
    SourcesReorderedOrToggled,
    SourceRegistered,
    SourceUnregistered,
    SourceDataChanged
}

public sealed class ModelCapabilitiesChangedEventArgs : EventArgs
{
    public ModelCapabilitiesChangeReason Reason { get; init; }
    public IReadOnlyList<string> AffectedSourceIds { get; init; } = [];
    public ModelCapabilitySourceChangeKind? SourceKind { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
