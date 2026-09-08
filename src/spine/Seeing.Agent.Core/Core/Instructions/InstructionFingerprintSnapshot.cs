using System.Collections.ObjectModel;

namespace Seeing.Agent.Core.Instructions;

public sealed class InstructionFingerprintSnapshot
{
    public InstructionFingerprintSnapshot(
        string? cwd = null,
        IReadOnlyDictionary<string, string>? files = null)
    {
        Cwd = cwd ?? string.Empty;
        Files = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                files ?? new Dictionary<string, string>(),
                InstructionFingerprintStore.PathComparer));
    }

    public string Cwd { get; }

    public IReadOnlyDictionary<string, string> Files { get; }
}
