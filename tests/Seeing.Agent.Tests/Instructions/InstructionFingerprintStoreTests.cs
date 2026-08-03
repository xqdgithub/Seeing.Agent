using FluentAssertions;
using Seeing.Agent.Core.Instructions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

public sealed class InstructionFingerprintStoreTests
{
    [Fact]
    public void Diff_EmptySnapshot_ReturnsEveryDiscoveredFile()
    {
        var session = new SessionData();
        var discovered = new[]
        {
            File("/repo/AGENTS.md", "sha256:one"),
            File("/repo/src/AGENTS.md", "sha256:two")
        };

        var changed = InstructionFingerprintStore.Diff(
            InstructionFingerprintStore.Load(session),
            discovered);

        changed.Should().Equal(discovered);
    }

    [Fact]
    public void Diff_UnchangedFiles_ReturnsEmpty()
    {
        var session = new SessionData();
        var discovered = new[] { File("/repo/AGENTS.md", "sha256:one") };
        InstructionFingerprintStore.MergeAndSave(session, "/repo", discovered);

        var changed = InstructionFingerprintStore.Diff(
            InstructionFingerprintStore.Load(session),
            discovered);

        changed.Should().BeEmpty();
    }

    [Fact]
    public void Diff_ChangedHash_ReturnsOnlyChangedFile()
    {
        var session = new SessionData();
        InstructionFingerprintStore.MergeAndSave(
            session,
            "/repo",
            [File("/repo/AGENTS.md", "sha256:old"), File("/repo/src/AGENTS.md", "sha256:same")]);
        var discovered = new[]
        {
            File("/repo/AGENTS.md", "sha256:new"),
            File("/repo/src/AGENTS.md", "sha256:same")
        };

        var changed = InstructionFingerprintStore.Diff(
            InstructionFingerprintStore.Load(session),
            discovered);

        changed.Should().ContainSingle().Which.Should().BeSameAs(discovered[0]);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptySnapshot()
    {
        var session = new SessionData();
        session.Metadata[ProjectInstructions.FingerprintMetadataKey] = "{not json";

        var snapshot = InstructionFingerprintStore.Load(session);

        snapshot.Cwd.Should().BeEmpty();
        snapshot.Files.Should().BeEmpty();
    }

    [Fact]
    public void MergeAndSave_MergesFilesAndUpdatesCwd()
    {
        var session = new SessionData();
        InstructionFingerprintStore.MergeAndSave(
            session,
            "/repo",
            [File("/repo/AGENTS.md", "sha256:one")]);

        InstructionFingerprintStore.MergeAndSave(
            session,
            "/repo/src",
            [File("/repo/src/AGENTS.md", "sha256:two")]);

        var snapshot = InstructionFingerprintStore.Load(session);
        snapshot.Cwd.Should().Be("/repo/src");
        snapshot.Files.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["/repo/AGENTS.md"] = "sha256:one",
            ["/repo/src/AGENTS.md"] = "sha256:two"
        });
    }

    private static InstructionFile File(string path, string hash) =>
        new() { Path = path, Hash = hash, Content = path };
}
