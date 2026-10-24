using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 装配器行为锁定：diff / 增量叙述 / 只读 / 字节稳定 / 版本二分 / 并发。
/// </summary>
public sealed class ContextAssemblerTests
{
    private static ContextAssembler CreateAssembler(SessionData session, params IContextSource[] sources)
    {
        var manager = new Mock<ISessionManager>();
        manager
            .Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        return new ContextAssembler(sources, manager.Object, NullLogger<ContextAssembler>.Instance);
    }

    private static async Task<ContextAssemblyResult> AssembleAsync(
        ContextAssembler assembler,
        SessionData session,
        string? insertBeforeMessageId = null,
        ContextAssemblyMode mode = ContextAssemblyMode.Persist)
        => await assembler.EnsureFreshAsync(
            session,
            mode,
            insertBeforeMessageId,
            skip: null,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task NoChange_ShouldNotEmitMessage()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        // 首次装配建立基线（System 落点直接进入提示词，不产生历史消息）
        await AssembleAsync(assembler, session);
        var countAfterBaseline = session.MessageCount;

        var result = await AssembleAsync(assembler, session);

        result.DeltaText.Should().BeNull();
        result.ChangedSourceKeys.Should().BeEmpty();
        session.MessageCount.Should().Be(countAfterBaseline);
        session.Metadata.Should().ContainKey(SessionMetadataKeys.ContextBaseline);
    }

    [Fact]
    public async Task Changed_ShouldEmitExactlyOneMessage()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var anchor = SessionMessage.UserMessage("hello");
        session.AddMessage(anchor);

        source.Value = FakeSource.ValueOf("""{"items":["a","b"]}""");
        var result = await AssembleAsync(assembler, session, anchor.Id);

        result.DeltaText.Should().NotBeNull();
        result.ChangedSourceKeys.Should().ContainSingle().Which.Should().Be("fake");
        session.MessageCount.Should().Be(2);

        var delta = session.Messages[0];
        delta.Role.Should().Be(MessageRole.User);
        delta.Content.Should().Contain("""<context-update source="fake">""");
        delta.Content.Should().Contain("Fake changed:");
        delta.Content.Should().Contain("""{"items":["a","b"]}""");
        session.Messages[1].Should().BeSameAs(anchor);
    }

    [Fact]
    public async Task RenderReturningNull_ShouldNotAdvanceCurrentValues()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        source.RenderBehavior = (_, _) => null;

        var silent = await AssembleAsync(assembler, session);

        silent.DeltaText.Should().BeNull();
        session.MessageCount.Should().Be(0);
        // 检测到变化，但没有可叙述文本
        silent.ChangedSourceKeys.Should().Contain("fake");

        // 下一次仍重试同一变化，证明 currentValues 没有被推进
        source.RenderBehavior = null;
        var retried = await AssembleAsync(assembler, session);

        retried.DeltaText.Should().NotBeNull();
        session.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Unavailable_ShouldKeepPreviousValuesAndNotNarrate()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        source.Value = FakeSource.Unavailable();
        var result = await AssembleAsync(assembler, session);

        result.DeltaText.Should().BeNull();
        result.ChangedSourceKeys.Should().BeEmpty();
        result.SkippedSourceKeys.Should().Contain("fake");
        session.MessageCount.Should().Be(0);

        // 恢复成同一旧值：不应叙述
        source.Value = FakeSource.ValueOf("""{"items":["a"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().BeNull();

        // 读取抛异常 → 映射为 Unavailable，且必须记入 Issues（禁止静默吞异常）
        var throwing = new FakeSource { Key = new("throwing"), ThrowOnRead = true };
        var session2 = new SessionData { Id = "s2" };
        var throwingResult = await AssembleAsync(CreateAssembler(session2, throwing), session2);

        throwingResult.SkippedSourceKeys.Should().Contain("throwing");
        throwingResult.Issues.Should().Contain(issue => issue.Contains("throwing"));
    }

    [Fact]
    public async Task Removed_ShouldNarrateMustNotBeUsed()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        ContextValueState? previousState = null;
        source.Value = FakeSource.Removed();
        source.RenderBehavior = (previous, _) =>
        {
            previousState = previous.State;
            return "Fake 已删除，must not be used。";
        };

        var result = await AssembleAsync(assembler, session);

        previousState.Should().Be(ContextValueState.Value);
        result.DeltaText.Should().Contain("must not be used");
        session.MessageCount.Should().Be(1);

        // 删除只叙述一次
        (await AssembleAsync(assembler, session)).DeltaText.Should().BeNull();
    }

    [Fact]
    public async Task BuildSystemBaseline_ShouldBeByteIdenticalAcrossCalls()
    {
        var system = new FakeSource { Key = new("a.system") };
        var message = new FakeSource
        {
            Key = new("b.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["m"]}""")
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, system, message);

        var assembled = await AssembleAsync(assembler, session);

        var first = assembler.BuildSystemBaseline(session);
        var second = assembler.BuildSystemBaseline(session);

        first.Should().Be(second);
        assembled.SystemBaseline.Should().Be(first);
        // Message 落点的初始渲染走历史，不进系统提示词
        first.Should().Contain("""{"items":["a"]}""").And.NotContain("""{"items":["m"]}""");
        assembled.HistoryBaseline.Should().NotBeNull().And.Contain("""{"items":["m"]}""");

        // 只读已存状态：源被禁止读取时仍必须返回同一字节
        system.ThrowOnRead = true;
        message.ThrowOnRead = true;
        assembler.BuildSystemBaseline(session).Should().Be(first);
    }

    [Fact]
    public async Task ValueSchemaVersionMismatch_ShouldRebuildAndNarrateFull()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        assembler.BuildSystemBaseline(session).Should().Contain("""{"items":["a"]}""");

        ContextValueState? previousState = null;
        source.ValueSchemaVersion = 2;
        source.Value = FakeSource.ValueOf("""{"items":["c"]}""");
        source.RenderBehavior = (previous, current) =>
        {
            previousState = previous.State;
            return $"FULL {CanonicalJson.Serialize(current.Canonical!.Value)}";
        };

        var result = await AssembleAsync(assembler, session);

        // 版本不匹配按 Initial 语义全量叙述
        previousState.Should().Be(ContextValueState.Initial);
        result.DeltaText.Should().Contain("FULL").And.Contain("""{"items":["c"]}""");
        assembler.BuildSystemBaseline(session)
            .Should().Contain("""{"items":["c"]}""")
            .And.NotContain("""{"items":["a"]}""");
        session.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadOnlyMode_ShouldNotMutateSessionOrState()
    {
        var system = new FakeSource();
        var message = new FakeSource
        {
            Key = new("b.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["m"]}""")
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, system, message);

        var first = await AssembleAsync(assembler, session, mode: ContextAssemblyMode.ReadOnly);
        var second = await AssembleAsync(assembler, session, mode: ContextAssemblyMode.ReadOnly);

        first.SystemBaseline.Should().NotBeNullOrEmpty().And.Be(second.SystemBaseline);
        first.HistoryBaseline.Should().NotBeNull().And.Contain("""{"items":["m"]}""");
        session.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
        session.MessageCount.Should().Be(0);
        session.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentCalls_ShouldAdvanceOnce()
    {
        var source = new FakeSource();
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        source.Value = FakeSource.ValueOf("""{"items":["z"]}""");

        var results = await Task.WhenAll(
            Task.Run(() => assembler.EnsureFreshAsync(
                session,
                ContextAssemblyMode.Persist,
                null,
                null,
                TestContext.Current.CancellationToken)),
            Task.Run(() => assembler.EnsureFreshAsync(
                session,
                ContextAssemblyMode.Persist,
                null,
                null,
                TestContext.Current.CancellationToken)));

        // 同一会话只推进一次：一条增量消息、且只有一次真正叙述
        session.MessageCount.Should().Be(1);
        results.Count(r => r.DeltaText is not null).Should().Be(1);
    }
}

/// <summary>
/// 测试假源：只依赖 Abstractions 契约，不引入指令 / 技能实现。
/// </summary>
file sealed class FakeSource : IContextSource
{
    public ContextSourceKey Key { get; init; } = new("fake");

    public ContextSourceScope Scope { get; init; } = ContextSourceScope.Project;

    public int ValueSchemaVersion { get; set; } = 1;

    public ContextSourceTarget Target { get; init; } = ContextSourceTarget.System;

    public int MaxBaselineChars => 0;

    /// <summary>当前读取结果；测试可直接替换以模拟运行时变化。</summary>
    public ContextSourceValue Value { get; set; } = ValueOf("""{"items":["a"]}""");

    /// <summary>自定义增量渲染；null 表示用默认文案。</summary>
    public Func<ContextSourceValue, ContextSourceValue, string?>? RenderBehavior { get; set; }

    /// <summary>为 true 时读取抛异常（验证异常映射为 Unavailable 并进入 Issues）。</summary>
    public bool ThrowOnRead { get; set; }

    public int ReadCount { get; private set; }

    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ReadCount++;
        return ThrowOnRead
            ? Task.FromException<ContextSourceValue>(new IOException("读取失败"))
            : Task.FromResult(Value);
    }

    public string RenderInitial(ContextSourceValue value) => $"## Fake\n{CanonicalText(value)}";

    public string? Render(ContextSourceValue previous, ContextSourceValue current) =>
        RenderBehavior is null
            ? $"Fake changed: {CanonicalText(current)}"
            : RenderBehavior(previous, current);

    public static ContextSourceValue ValueOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ContextSourceValue(ContextValueState.Value, document.RootElement.Clone(), null);
    }

    public static ContextSourceValue Unavailable() => new(ContextValueState.Unavailable, null, null);

    public static ContextSourceValue Removed() => new(ContextValueState.Removed, null, null);

    private static string CanonicalText(ContextSourceValue value) =>
        value.Canonical is { } canonical ? CanonicalJson.Serialize(canonical) : value.State.ToString();
}
