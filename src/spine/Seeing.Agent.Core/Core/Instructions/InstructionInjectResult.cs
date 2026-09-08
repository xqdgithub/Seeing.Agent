namespace Seeing.Agent.Core.Instructions;

public sealed class InstructionInjectResult
{
    public bool Injected { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> InjectedPaths { get; init; } = Array.Empty<string>();
}
