namespace Seeing.Agent.Memory.Abstractions;

public interface IEmbeddingStatus
{
    bool IsAvailable { get; }
    string? Reason { get; }
}
