using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
/// 迁移适配锁定：指令注入改为委托上下文装配器，指纹双轨不再写入；
/// 调用方 cwd 必须真正交给源（不得读进程当前目录或静默忽略签名参数）。
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
        var manager = CreateManager(session, CreateReader());

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Injected.Should().BeTrue();
        result.Reason.Should().Be(ProjectInstructions.Reasons.Initial);
        session.Messages.Should().ContainSingle();
        session.Messages[0].Content.Should().Contain("<project-instructions");
        session.Messages[0].Content.Should().Contain("root instructions");
        session.Messages[0].Content.Should().Contain("reason=\"initial\"");
        // I1：初始渲染消息必须携带既有 project-instructions 元数据（UI 折叠 + 标题服务判定的契约）
        session.Messages[0].Metadata.Should().ContainKey(ProjectInstructions.MetadataKeys.ProjectInstructions);
        session.Messages[0].Metadata![ProjectInstructions.MetadataKeys.Reason]
            .Should().Be(ProjectInstructions.Reasons.Initial);
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
        var manager = CreateManager(session, CreateReader());
        await manager.InjectIfNeededAsync(session, _root, _root, ct);

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Injected.Should().BeFalse();
        result.Reason.Should().Be(ProjectInstructions.Reasons.None);
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
        var manager = CreateManager(session, CreateReader());
        await manager.InjectIfNeededAsync(session, _root, _root, ct);
        await File.WriteAllTextAsync(seeingPath, "changed seeing instructions", ct);

        var result = await manager.InjectIfNeededAsync(session, _root, _root, ct);

        result.Reason.Should().Be(ProjectInstructions.Reasons.ContentChange);
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
        var manager = CreateManager(session, CreateReader());
        await manager.InjectIfNeededAsync(session, _root, _root, ct);

        var result = await manager.InjectIfNeededAsync(session, nested, _root, ct);

        // cwd 由端口真正生效（不再被静默忽略）：嵌套文件按增量叙述，原因记为 content-change
        result.Reason.Should().Be(ProjectInstructions.Reasons.ContentChange);
        session.Messages.Should().HaveCount(2);
        session.Messages[1].Content.Should().Contain("nested instructions");
        session.Messages[1].Content.Should().Contain(nestedPath);
    }

    [Fact]
    public async Task InjectIfNeededAsync_UsesCallerCwd_NotProcessDirectory()
    {
        // 进程当前目录是仓库根（存在 AGENTS.md）；若源读进程目录，就会注入仓库指令
        var ct = TestContext.Current.CancellationToken;
        var sessionCwd = Path.Combine(Path.GetTempPath(), $"seeing-session-cwd-{Guid.NewGuid():N}");
        sessionCwd = Path.GetFullPath(sessionCwd);
        var session = new SessionData { Id = Guid.NewGuid().ToString("N") };
        var reader = new RecordingInstructionFileReader("caller-cwd-marker");
        var manager = CreateManager(session, reader);

        var result = await manager.InjectIfNeededAsync(session, sessionCwd, sessionCwd, ct);

        result.Injected.Should().BeTrue();
        reader.LastCwd.Should().Be(sessionCwd, "源必须使用调用方（会话）cwd");
        reader.LastWorkspaceRoot.Should().Be(sessionCwd);
        session.Messages[0].Content.Should().Contain("caller-cwd-marker");
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
        var manager = CreateManager(session, CreateReader());

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

    private InstructionFileReader CreateReader() =>
        new(new InstructionDiscovery(
                NullLogger<InstructionDiscovery>.Instance,
                () => _userProfile),
            NullLogger<InstructionFileReader>.Instance);

    private static InstructionManager CreateManager(
        SessionData session,
        IInstructionFileReader reader)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(manager => manager.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        // scoped 管道：InstructionManager 每次调用创建短生命周期作用域（单例不得捕获 scoped 装配器）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISessionManager>(sessionManager.Object);
        services.AddScoped<ContextExecutionScope>();
        services.AddScoped<IContextExecutionScope>(sp => sp.GetRequiredService<ContextExecutionScope>());
        services.AddSingleton(reader);
        services.AddScoped<IContextSource, ProjectInstructionsContextSource>();
        services.AddScoped<IContextAssembler>(sp => new ContextAssembler(
            sp.GetServices<IContextSource>(),
            sp.GetRequiredService<ISessionManager>(),
            NullLogger<ContextAssembler>.Instance));

        var provider = services.BuildServiceProvider();

        return new InstructionManager(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InstructionManager>.Instance);
    }

    /// <summary>记录调用方根的假读取器（不碰文件系统）。</summary>
    private sealed class RecordingInstructionFileReader(string content) : IInstructionFileReader
    {
        public string? LastCwd { get; private set; }

        public string? LastWorkspaceRoot { get; private set; }

        public Task<IReadOnlyList<InstructionFileReadResult>> ReadAsync(
            string cwd,
            string workspaceRoot,
            CancellationToken ct = default)
        {
            LastCwd = cwd;
            LastWorkspaceRoot = workspaceRoot;

            return Task.FromResult<IReadOnlyList<InstructionFileReadResult>>(
            [
                new InstructionFileReadResult(
                    Path.Combine(cwd, "AGENTS.md"),
                    InstructionFileReadStatus.Loaded,
                    content,
                    null)
            ]);
        }
    }
}
