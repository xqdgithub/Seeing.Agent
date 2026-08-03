using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

internal static class InstructionFingerprintStore
{
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static InstructionFingerprintSnapshot Load(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Metadata is null ||
            !session.Metadata.TryGetValue(ProjectInstructions.FingerprintMetadataKey, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return new InstructionFingerprintSnapshot();
        }

        try
        {
            var state = JsonSerializer.Deserialize<FingerprintState>(json);
            return state is null
                ? new InstructionFingerprintSnapshot()
                : new InstructionFingerprintSnapshot(state.Cwd, state.Files);
        }
        catch (JsonException)
        {
            return new InstructionFingerprintSnapshot();
        }
    }

    public static IReadOnlyList<InstructionFile> Diff(
        InstructionFingerprintSnapshot snapshot,
        IReadOnlyList<InstructionFile> discovered)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(discovered);

        return discovered
            .Where(file =>
                !snapshot.Files.TryGetValue(file.Path, out var hash) ||
                !string.Equals(hash, file.Hash, StringComparison.Ordinal))
            .ToArray();
    }

    public static void MergeAndSave(
        SessionData session,
        string cwd,
        IReadOnlyList<InstructionFile> injectedFiles)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(cwd);
        ArgumentNullException.ThrowIfNull(injectedFiles);

        var current = Load(session);
        var merged = new Dictionary<string, string>(current.Files, PathComparer);
        foreach (var file in injectedFiles)
        {
            merged[file.Path] = file.Hash;
        }

        var json = JsonSerializer.Serialize(new FingerprintState
        {
            Cwd = cwd,
            Files = merged
        });

        session.Metadata ??= new Dictionary<string, string>();
        session.Metadata[ProjectInstructions.FingerprintMetadataKey] = json;
    }

    private sealed class FingerprintState
    {
        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public Dictionary<string, string> Files { get; set; } = new(PathComparer);
    }
}
