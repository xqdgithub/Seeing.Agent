using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Context;
using Seeing.Session;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 生命周期接线锁定：压缩置位 / 位集合合并 / 清空消息 / fork 不继承 / revert 不重复叙述。
/// </summary>
public sealed class ContextLifecycleTests
{
    private static readonly ContextBaselineStore s_store =
        new(NullLogger<ContextBaselineStore>.Instance);

    // ===== 压缩 =====

    [Fact]
    public async Task Compaction_ShouldSetPendingFlag_Once()
    {
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("你好"));
        var service = CreateCompressionService(session);

        var first = await service.CompressAsync("s1", "manual", TestContext.Current.CancellationToken);

        first.Success.Should().BeTrue();
        s_store.Load(session).Pending.Should().HaveFlag(PendingBaselineReset.Compaction);

        // 再次压缩：位集合幂等（不叠加、不清零）
        var second = await service.CompressAsync("s1", "manual", TestContext.Current.CancellationToken);

        second.Success.Should().BeTrue();
        s_store.Load(session).Pending.Should().Be(PendingBaselineReset.Compaction);
    }

    [Fact]
    public async Task CompactionThenWorkspaceChange_ShouldKeepBothFlags()
    {
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("你好"));

        // 先置「工作区变更」位，压缩不得覆盖它
        var state = s_store.Load(session);
        state.Pending |= PendingBaselineReset.WorkspaceChanged;
        s_store.Save(session, state);

        var service = CreateCompressionService(session);
        var outcome = await service.CompressAsync("s1", "manual", TestContext.Current.CancellationToken);

        outcome.Success.Should().BeTrue();
        var pending = s_store.Load(session).Pending;
        pending.Should().HaveFlag(PendingBaselineReset.Compaction);
        pending.Should().HaveFlag(PendingBaselineReset.WorkspaceChanged);
    }

    // ===== 清空消息 =====

    [Fact]
    public void ClearMessages_ShouldRemoveContextBaseline()
    {
        var session = new SessionData { Id = "s1" };
        session.AddMessage(SessionMessage.UserMessage("你好"));
        s_store.Save(session, new ContextBaselineState());
        session.Metadata.Should().ContainKey(SessionMetadataKeys.ContextBaseline);

        session.ClearMessages();

        session.Messages.Should().BeEmpty();
        session.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }

    // ===== fork =====

    [Fact]
    public async Task Fork_ShouldNotCarryContextBaseline()
    {
        // 分支属新 epoch：基线必须重建（本用例锁定「不复制」边界）
        var manager = new SessionManager(
            new InMemorySessionStore(),
            logger: NullLogger<SessionManager>.Instance);
        var source = manager.Create(partitionId: "p1", selectedAgent: "build");
        source.AddMessage(SessionMessage.UserMessage("你好"));
        s_store.Save(source, new ContextBaselineState());

        var forker = new SessionForker(NullLogger<SessionForker>.Instance, manager);
        var forked = await forker.ForkAsync(source.Id, "branch", TestContext.Current.CancellationToken);

        forked.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }

    // ===== revert =====

    [Fact]
    public async Task RevertBetweenTwoDeltas_ShouldNotRepeatNarration()
    {
        var source = new LifecycleFakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        // 建立基线 + 两次增量
        await AssembleAsync(assembler, session);
        source.Value = LifecycleFakeSource.ValueOf("""{"items":["a","b"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().NotBeNull();
        var firstDeltaId = session.Messages[^1].Id;

        source.Value = LifecycleFakeSource.ValueOf("""{"items":["a","b","c"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().NotBeNull();
        var secondDelta = session.Messages[^1];
        var countWithTwoDeltas = session.MessageCount;

        // 回退到两条增量之间：删除第二条增量消息
        session.RemoveMessage(secondDelta);
        session.MessageCount.Should().Be(countWithTwoDeltas - 1);

        // 当前值回到第一条增量叙述的值：不得重复叙述
        source.Value = LifecycleFakeSource.ValueOf("""{"items":["a","b"]}""");
        var reverted = await AssembleAsync(assembler, session);

        reverted.DeltaText.Should().BeNull("回退到已叙述过的值时不得重复叙述");
        session.MessageCount.Should().Be(countWithTwoDeltas - 1);
        session.Messages[^1].Id.Should().Be(firstDeltaId);

        // 继续前进仍只叙述一次
        source.Value = LifecycleFakeSource.ValueOf("""{"items":["a","b","c"]}""");
        var forward = await AssembleAsync(assembler, session);

        forward.DeltaText.Should().NotBeNull().And.Contain("\"c\"");
        session.MessageCount.Should().Be(countWithTwoDeltas);
    }

    // ===== 辅助 =====

    private static CompressionService CreateCompressionService(SessionData session)
    {
        var summarizer = new Mock<ISummarizer>();
        summarizer
            .Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummarizeRequest request, CancellationToken _) => new SummarizeResult(
                Summary: "摘要",
                ResultMessages: [SessionMessage.AssistantMessage("摘要")],
                SummaryTokenCount: 2,
                MessagesRemoved: 1));

        var manager = new Mock<ISessionManager>();
        manager
            .Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        manager
            .Setup(m => m.SaveAndNotifyAsync(session.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new CompressionService(
            summarizer.Object,
            manager.Object,
            NullLogger<CompressionService>.Instance);
    }

    private static ContextAssembler CreateAssembler(SessionData session, IContextSource source)
    {
        var manager = new Mock<ISessionManager>();
        manager
            .Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        return new ContextAssembler(
            [source],
            manager.Object,
            NullLogger<ContextAssembler>.Instance);
    }

    private static Task<ContextAssemblyResult> AssembleAsync(
        ContextAssembler assembler,
        SessionData session)
        => assembler.EnsureFreshAsync(
            session,
            ContextAssemblyMode.Persist,
            insertBeforeMessageId: null,
            skip: null,
            TestContext.Current.CancellationToken);
}

/// <summary>生命周期测试用假源（System 落点，值可直接替换）。</summary>
file sealed class LifecycleFakeSource : IContextSource
{
    public ContextSourceKey Key => new("fake");

    public ContextSourceScope Scope => ContextSourceScope.Project;

    public int ValueSchemaVersion => 1;

    public ContextSourceTarget Target => ContextSourceTarget.System;

    public int MaxBaselineChars => 0;

    public ContextSourceValue Value { get; set; } = ValueOf("""{"items":["a"]}""");

    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default) => Task.FromResult(Value);

    public string RenderInitial(ContextSourceValue value) =>
        $"## Fake\n{(value.Canonical is { } canonical ? CanonicalJson.Serialize(canonical) : string.Empty)}";

    public string? Render(ContextSourceValue previous, ContextSourceValue current) =>
        $"Fake changed: {(current.Canonical is { } canonical ? CanonicalJson.Serialize(canonical) : string.Empty)}";

    public static ContextSourceValue ValueOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ContextSourceValue(ContextValueState.Value, document.RootElement.Clone(), null);
    }
}
