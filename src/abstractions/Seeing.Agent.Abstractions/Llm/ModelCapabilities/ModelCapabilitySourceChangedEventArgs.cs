namespace Seeing.Agent.Abstractions.Llm;

public enum ModelCapabilitySourceChangeKind
{
    Reloaded,
    EntriesEdited,
    AliasesEdited,
    RefreshFailed,
    LoadFailed
}

public sealed class ModelCapabilitySourceChangedEventArgs : EventArgs
{
    public required string SourceId { get; init; }
    public ModelCapabilitySourceChangeKind Kind { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
