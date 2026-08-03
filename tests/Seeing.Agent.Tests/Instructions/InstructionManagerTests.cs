using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Instructions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

public sealed class InstructionManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-instruction-manager-{Guid.NewGuid():N}");

    [Fact]
    public async Task InjectIfNeededAsync_FirstDiscovery_AddsMessageAndSavesFingerprints()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(path, "root instructions", ct);
        var session = new SessionData();
        var manager = CreateManager();

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Injected.Should().BeTrue();
        result.Reason.Should().Be(ProjectInstructions.Reasons.Initial);
        result.InjectedPaths.Should().Equal(path);
        session.Messages.Should().ContainSingle();
        session.Messages[0].Metadata![ProjectInstructions.MetadataKeys.Reason]
            .Should().Be(ProjectInstructions.Reasons.Initial);
        manager.GetFingerprints(session).Files.Should().ContainKey(path);
    }

    [Fact]
    public async Task InjectIfNeededAsync_UnchangedDiscovery_DoesNotAddMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions", ct);
        var session = new SessionData();
        var manager = CreateManager();
        await manager.InjectIfNeededAsync(session, _root, _root, ct);

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Injected.Should().BeFalse();
        result.Reason.Should().Be(ProjectInstructions.Reasons.None);
        result.InjectedPaths.Should().BeEmpty();
        session.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task InjectIfNeededAsync_ChangedContent_InjectsOnlyChangedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var rootPath = Path.Combine(_root, "AGENTS.md");
        var seeingDirectory = Path.Combine(_root, ".seeing");
        Directory.CreateDirectory(seeingDirectory);
        var seeingPath = Path.Combine(seeingDirectory, "AGENTS.md");
        await File.WriteAllTextAsync(rootPath, "root instructions", ct);
        await File.WriteAllTextAsync(seeingPath, "seeing instructions", ct);
        var session = new SessionData();
        var manager = CreateManager();
        await manager.InjectIfNeededAsync(session, _root, _root, ct);
        await File.WriteAllTextAsync(seeingPath, "changed seeing instructions", ct);

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Reason.Should().Be(ProjectInstructions.Reasons.ContentChange);
        result.InjectedPaths.Should().Equal(seeingPath);
        session.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task InjectIfNeededAsync_DeeperCwd_InjectsNewNestedFileWithCwdChangeReason()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions", ct);
        var nested = Path.Combine(_root, "src");
        Directory.CreateDirectory(nested);
        var nestedPath = Path.Combine(nested, "AGENTS.md");
        await File.WriteAllTextAsync(nestedPath, "nested instructions", ct);
        var session = new SessionData();
        var manager = CreateManager();
        await manager.InjectIfNeededAsync(session, _root, _root, ct);

        var result = await manager.InjectIfNeededAsync(session, nested, _root, ct);

        result.Reason.Should().Be(ProjectInstructions.Reasons.CwdChange);
        result.InjectedPaths.Should().Equal(nestedPath);
        manager.GetFingerprints(session).Cwd.Should().Be(Path.GetFullPath(nested));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static InstructionManager CreateManager() =>
        new(NullLogger<InstructionManager>.Instance, NullLoggerFactory.Instance);
}
