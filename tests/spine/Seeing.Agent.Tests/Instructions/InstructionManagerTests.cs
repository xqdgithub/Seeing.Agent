using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Instructions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

/// <summary>
/// 迁移适配锁定：指令注入改为委托上下文装配器，指纹双轨不再写入。
/// </summary>
public sealed class InstructionManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-instruction-manager-{Guid.NewGuid():N}");

    private readonly string _userProfile =
        Path.Combine(Path.GetTempPath(), $"seeing-instruction-user-{Guid.NewGuid():N}");

    [Fact]
    public async Task InjectIfNeededAsync_FirstDiscovery_AddsMessageAndStoresBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_userProfile);
        var path = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(path, "root instructions", ct);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var manager = CreateManager(session, () => (_root, _root));

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Injected.Should().BeTrue();
        result.Reason.Should().Be(ProjectInstructions.Reasons.Initial);
        result.InjectedPaths.Should().Equal(path);
        session.Messages.Should().ContainSingle();
        session.Messages[0].Content.Should().Contain("<project-instructions");
        session.Messages[0].Content.Should().Contain("root instructions");
        session.Messages[0].Content.Should().Contain("reason=\"initial\"");
        session.Metadata.Should().ContainKey(SessionMetadataKeys.ContextBaseline);
    }

    [Fact]
    public async Task InjectIfNeededAsync_UnchangedDiscovery_DoesNotAddMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_userProfile);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions", ct);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var manager = CreateManager(session, () => (_root, _root));
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
        Directory.CreateDirectory(_userProfile);
        var rootPath = Path.Combine(_root, "AGENTS.md");
        var seeingDirectory = Path.Combine(_root, ".seeing");
        Directory.CreateDirectory(seeingDirectory);
        var seeingPath = Path.Combine(seeingDirectory, "AGENTS.md");
        await File.WriteAllTextAsync(rootPath, "root instructions", ct);
        await File.WriteAllTextAsync(seeingPath, "seeing instructions", ct);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var manager = CreateManager(session, () => (_root, _root));
        await manager.InjectIfNeededAsync(session, _root, _root, ct);
        await File.WriteAllTextAsync(seeingPath, "changed seeing instructions", ct);

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Reason.Should().Be(ProjectInstructions.Reasons.ContentChange);
        result.InjectedPaths.Should().Equal(seeingPath);
        session.Messages.Should().HaveCount(2);
        session.Messages[1].Content.Should().Contain("changed seeing instructions");
        session.Messages[1].Content.Should().NotContain("root instructions");
    }

    [Fact]
    public async Task InjectIfNeededAsync_RootsChanged_InjectsNewNestedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_userProfile);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions", ct);
        var nested = Path.Combine(_root, "src");
        Directory.CreateDirectory(nested);
        var nestedPath = Path.Combine(nested, "AGENTS.md");
        await File.WriteAllTextAsync(nestedPath, "nested instructions", ct);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var roots = (_root, _root);
        var manager = CreateManager(session, () => roots);
        await manager.InjectIfNeededAsync(session, _root, _root, ct);

        roots = (nested, _root);
        var result = await manager.InjectIfNeededAsync(session, nested, _root, ct);

        // cwd 由源解析；新增文件按增量叙述，原因记为 content-change
        result.Reason.Should().Be(ProjectInstructions.Reasons.ContentChange);
        result.InjectedPaths.Should().Equal(nestedPath);
        session.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task InjectIfNeededAsync_WithAnchor_InsertsBeforeUserCommandMessage()
    {
        // 用户命令先落盘、指令后注入；锚点命中时指令须插入到用户命令之前
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_userProfile);
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions", ct);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var userCommand = SessionMessage.UserMessage("/skill demo");
        session.AddMessage(userCommand);
        var manager = CreateManager(session, () => (_root, _root));

        var result = await manager.InjectIfNeededAsync(
            session, _root, _root, ct, insertBeforeMessageId: userCommand.Id);

        result.Injected.Should().BeTrue();
        session.Messages.Should().HaveCount(2);
        session.Messages[0].Content.Should().Contain("<project-instructions");
        session.Messages[1].Content.Should().Be("/skill demo", "用户命令应保持在指令之后（末条）");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_userProfile))
        {
            Directory.Delete(_userProfile, recursive: true);
        }
    }

    private InstructionManager CreateManager(
        SessionData session,
        Func<(string Cwd, string WorkspaceRoot)> roots)
    {
        var reader = new InstructionFileReader(
            NullLogger<InstructionFileReader>.Instance,
            NullLoggerFactory.Instance,
            () => _userProfile);

        var source = new ProjectInstructionsContextSource(
            reader,
            roots,
            NullLogger<ProjectInstructionsContextSource>.Instance);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(manager => manager.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var assembler = new ContextAssembler(
            [source],
            sessionManager.Object,
            NullLogger<ContextAssembler>.Instance);

        return new InstructionManager(
            assembler,
            NullLogger<InstructionManager>.Instance,
            NullLoggerFactory.Instance);
    }
}
