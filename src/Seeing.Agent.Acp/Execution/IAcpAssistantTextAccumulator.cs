namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// ACP update sink that accumulates streamed assistant text from mapped session updates.
/// </summary>
public interface IAcpAssistantTextAccumulator
{
    /// <summary>Concatenated <see cref="Seeing.Agent.Core.Events.StreamDeltaEvent.ContentDelta"/> text.</summary>
    string AccumulatedAssistantText { get; }
}
