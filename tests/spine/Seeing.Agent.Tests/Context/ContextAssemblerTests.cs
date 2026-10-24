using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Core.Prompts;
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

    // ===== I9：核心不变量 =====

    [Fact]
    public async Task ChangedValues_ShouldKeepSystemBaselineByteIdentical()
    {
        // 设计的首要目标：内容变化不击穿前缀缓存（System 基线不变，变化只走增量消息）
        var source = new FakeSource { Key = new("a.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var baselineBefore = assembler.BuildSystemBaseline(session);
        var promptBuilder = new PromptBuilder([]);
        var promptBefore = await promptBuilder.BuildAsync(
            new PromptContext
            {
                Agent = new AgentDefinition { SystemPrompt = "BODY" },
                ContextBaseline = baselineBefore
            },
            TestContext.Current.CancellationToken);

        source.Value = FakeSource.ValueOf("""{"items":["a","b"]}""");
        var assembled = await AssembleAsync(assembler, session);

        assembled.DeltaText.Should().NotBeNull();
        assembler.BuildSystemBaseline(session).Should().Be(baselineBefore);

        var promptAfter = await promptBuilder.BuildAsync(
            new PromptContext
            {
                Agent = new AgentDefinition { SystemPrompt = "BODY" },
                ContextBaseline = assembler.BuildSystemBaseline(session)
            },
            TestContext.Current.CancellationToken);

        promptAfter.Should().Be(promptBefore, "同一状态两次装配 + 提示词构建必须逐字节相同");

        // 同一状态连续两次构建：逐字节相同
        var promptAgain = await promptBuilder.BuildAsync(
            new PromptContext
            {
                Agent = new AgentDefinition { SystemPrompt = "BODY" },
                ContextBaseline = assembler.BuildSystemBaseline(session)
            },
            TestContext.Current.CancellationToken);

        promptAgain.Should().Be(promptAfter);
    }

    [Fact]
    public async Task SourceTextWithPlaceholders_ShouldNotBeSubstitutedInPrompt()
    {
        // Review Focus #2：源渲染文本里的 {{timestamp}} / {{model}} 必须原样保留
        var source = new FakeSource
        {
            Key = new("a.system"),
            RenderInitialBehavior = _ => "## Fake\nliteral {{timestamp}} and {{model}}"
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var prompt = await new PromptBuilder([]).BuildAsync(
            new PromptContext
            {
                Agent = new AgentDefinition { SystemPrompt = "BODY {{timestamp}}" },
                ContextBaseline = assembler.BuildSystemBaseline(session),
                ModelName = "provider/model",
                Timestamp = DateTime.Now
            },
            TestContext.Current.CancellationToken);

        prompt.Should().Contain("literal {{timestamp}} and {{model}}");
    }

    [Fact]
    public async Task PersistedState_ShouldNotContainPayloadBody()
    {
        // spec §6 硬不变量：Payload 每次重新取得、不落盘
        const string payloadBody = "PAYLOAD-BODY-MUST-NOT-PERSIST";
        var source = new FakeSource
        {
            Key = new("a.system"),
            Payload = new { Body = payloadBody }
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var persisted = session.Metadata[SessionMetadataKeys.ContextBaseline];
        persisted.Should().NotContain(payloadBody);
        persisted.Should().NotContain("payload");
        assembler.BuildSystemBaseline(session).Should().Contain("""{"items":["a"]}""");
    }

    [Fact]
    public async Task NondeterministicRerender_ShouldKeepStoredTextAndReportIssue()
    {
        // spec §8.3 第二分支：同版本同值却渲染出不同文本 → 沿用已存文本 + 告警
        var renders = 0;
        var source = new FakeSource
        {
            Key = new("a.system"),
            RenderInitialBehavior = _ => $"## Fake v{renders++}"
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var result = await AssembleAsync(assembler, session);

        result.Issues.Should().Contain(issue => issue.Contains("不确定"));
        assembler.BuildSystemBaseline(session).Should().Contain("v0");
        session.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Compaction_WithChangedContent_ShouldReanchorToCurrentText()
    {
        // C1：压缩后必须用**当前值**重新锚定基线；重放首次文本会让之后的修改永久不可见
        var source = new FakeSource
        {
            Key = new("a.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["a"]}""")
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        // 建立基线（A）→ 改内容产生增量（B）
        await AssembleAsync(assembler, session);
        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().Contain("\"b\"");

        // 压缩：置位，并清空历史（摘要取代了原消息）
        var store = new ContextBaselineStore(NullLogger<ContextBaselineStore>.Instance);
        var state = store.Load(session);
        state.Pending |= PendingBaselineReset.Compaction;
        store.Save(session, state);
        foreach (var message in session.Messages.ToList())
            session.RemoveMessage(message);

        var compacted = await AssembleAsync(assembler, session);

        // 新基线来自当前值 B，不是首次的 A
        compacted.Issues.Should().BeEmpty("epoch 变化不是「渲染不确定」，不得产生误导性告警");
        compacted.HistoryBaseline.Should().Contain("\"b\"").And.NotContain("\"a\"");
        // 重新锚定的源本轮不得再产生增量叙述
        compacted.DeltaText.Should().BeNull();

        var reanchoredState = store.Load(session).Sources["a.message"];
        reanchoredState.BaselineText.Should().Contain("\"b\"").And.NotContain("\"a\"");
        reanchoredState.BaselineValues!.ToJsonString().Should().Contain("\"b\"");
        reanchoredState.CurrentValues!.ToJsonString().Should().Contain("\"b\"");

        // 之后的变化必须相对新基线（B）叙述；同一装配不得再报「同值不同文本」
        string? previousCanonical = null;
        source.RenderBehavior = (previous, _) =>
        {
            previousCanonical = previous.Canonical is { } value ? CanonicalJson.Serialize(value) : null;
            return "CHANGED-TO-C";
        };
        source.Value = FakeSource.ValueOf("""{"items":["c"]}""");

        var next = await AssembleAsync(assembler, session);

        next.Issues.Should().BeEmpty();
        next.DeltaText.Should().Contain("CHANGED-TO-C");
        previousCanonical.Should().Contain("\"b\"", "增量必须相对压缩后的新基线叙述");
    }

    [Fact]
    public async Task RevertPastLastDelta_ShouldFoldToBaselineAndRenarrate()
    {
        // C2：回退掉全部增量后，已叙述值必须折回基线，否则模型看不到却永远不再补叙
        var source = new FakeSource { Key = new("a.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        await AssembleAsync(assembler, session);

        var anchor = SessionMessage.UserMessage("user");
        session.AddMessage(anchor);

        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        (await AssembleAsync(assembler, session, anchor.Id)).DeltaText.Should().NotBeNull();

        // 回退：删除唯一一条增量消息
        var deltaMessage = session.Messages.First(message =>
            message.Content?.Contains("<context-update", StringComparison.Ordinal) == true);
        session.RemoveMessage(deltaMessage);

        // 当前值仍是 B，但模型只剩基线 A → 必须重新叙述
        var renarrated = await AssembleAsync(assembler, session, anchor.Id);
        renarrated.DeltaText.Should().NotBeNull().And.Contain("\"b\"");
        session.MessageCount.Should().Be(2);

        // 相同装配不得重复叙述
        var stable = await AssembleAsync(assembler, session, anchor.Id);
        stable.DeltaText.Should().BeNull();
        session.MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task RevertAllMessageNarrations_ShouldFoldToBaselineAndRenarrate()
    {
        // C2（Message 落点）：初始消息与增量都被回退后，重放基线 + 重新叙述当前差异
        var source = new FakeSource
        {
            Key = new("a.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["a"]}""")
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        await AssembleAsync(assembler, session);
        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().NotBeNull();
        session.MessageCount.Should().Be(2);

        // 回退掉初始消息与增量（等价于回到压缩前状态）
        foreach (var message in session.Messages.ToList())
            session.RemoveMessage(message);

        var result = await AssembleAsync(assembler, session);

        // 初始消息重放基线（A），增量重新叙述当前值（B）
        result.HistoryBaseline.Should().Contain("\"a\"").And.NotContain("\"b\"");
        result.DeltaText.Should().NotBeNull().And.Contain("\"b\"");
        session.Messages[0].Content.Should().Contain("\"a\"");

        var stable = await AssembleAsync(assembler, session);
        stable.DeltaText.Should().BeNull("相同的已叙述状态不得重复叙述");
    }

    [Fact]
    public async Task Compaction_WithRemovedSource_ShouldClearBaseline()
    {
        // C1：压缩后「确认消失」的源必须锚定为空基线，不能把旧内容继续留在系统提示词里
        var source = new FakeSource { Key = new("a.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        await AssembleAsync(assembler, session);
        assembler.BuildSystemBaseline(session).Should().Contain("\"a\"");

        var store = new ContextBaselineStore(NullLogger<ContextBaselineStore>.Instance);
        var state = store.Load(session);
        state.Pending |= PendingBaselineReset.Compaction;
        store.Save(session, state);

        // 源已确认消失（Removed）；FakeSource 的 RenderInitial 对空对象仍返回标题 + {}，这里模拟真实源的空渲染
        source.Value = FakeSource.Removed();
        source.RenderInitialBehavior = _ => string.Empty;

        var result = await AssembleAsync(assembler, session);

        result.DeltaText.Should().BeNull("重新锚定的源本轮不得叙述增量");
        assembler.BuildSystemBaseline(session).Should().BeEmpty();

        // 下一轮不得反复尝试重放失败（Issues 为空、无增量）
        var next = await AssembleAsync(assembler, session);
        next.DeltaText.Should().BeNull();
        next.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipSet_ShouldLeaveSourceUntouched()
    {
        var source = new FakeSource { Key = new("a.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        var result = await assembler.EnsureFreshAsync(
            session,
            ContextAssemblyMode.Persist,
            insertBeforeMessageId: null,
            skip: new HashSet<ContextSourceKey> { source.Key },
            TestContext.Current.CancellationToken);

        result.SkippedSourceKeys.Should().Contain("a.system");
        result.ChangedSourceKeys.Should().BeEmpty();
        session.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }

    // ===== I3 / I4 / I5：语义与时序 =====

    [Fact]
    public async Task Compaction_WithUnavailableSource_ShouldReplayStoredText()
    {
        // I3：重放必须用存库文本；源不可读时也不能让指令永久消失
        var source = new FakeSource
        {
            Key = new("a.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["m"]}""")
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);

        var first = await AssembleAsync(assembler, session);
        var stored = first.HistoryBaseline;
        stored.Should().NotBeNull();
        session.MessageCount.Should().Be(1);

        var store = new ContextBaselineStore(NullLogger<ContextBaselineStore>.Instance);
        var state = store.Load(session);
        state.Pending |= PendingBaselineReset.Compaction;
        store.Save(session, state);

        source.Value = FakeSource.Unavailable();
        source.RenderInitialBehavior = _ => "MUTATED-RENDER-MUST-NOT-APPEAR";

        var result = await AssembleAsync(assembler, session);

        result.HistoryBaseline.Should().Be(stored);
        session.MessageCount.Should().Be(2);
        session.Messages[^1].Content.Should().Be(stored);
        session.Messages[^1].Content.Should().NotContain("MUTATED-RENDER");
    }

    [Fact]
    public async Task Compaction_WithUnavailableSource_ShouldKeepBaselineAndRetryReanchor()
    {
        // N2：压缩时源不可读 → 基线与值原样保留（不做部分更新）+ 按源标记待重锚定；
        // 恢复可读后下一次装配重锚定到当前内容，且不得产生「同值不同文本」误导告警或增量重复。
        var rendered = "## Fake A";
        var renderCount = 0;
        var source = new FakeSource
        {
            Key = new("a.system"),
            RenderInitialBehavior = _ =>
            {
                renderCount++;
                return rendered;
            }
        };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        var store = new ContextBaselineStore(NullLogger<ContextBaselineStore>.Instance);

        // 建立基线 A → 内容变为 B 并产生增量叙述（CurrentValues 已推进到 B）
        await AssembleAsync(assembler, session);
        var baselineTextA = store.Load(session).Sources["a.system"].BaselineText;
        baselineTextA.Should().Be("## Fake A");
        var baselineHashA = store.Load(session).Sources["a.system"].BaselineHash;

        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        (await AssembleAsync(assembler, session)).DeltaText.Should().NotBeNull();

        // 压缩（System 落点无需移除消息）+ 源恰好不可读
        var state = store.Load(session);
        state.Pending |= PendingBaselineReset.Compaction;
        store.Save(session, state);

        source.Value = FakeSource.Unavailable();
        rendered = "MUTATED-RENDER-MUST-NOT-APPEAR";

        var unavailable = await AssembleAsync(assembler, session);

        unavailable.Issues.Should().BeEmpty("不可读只是暂时状态，不得记录误导性告警");
        assembler.BuildSystemBaseline(session).Should().Be("## Fake A", "旧文本必须继续可用");
        renderCount.Should().Be(1, "不可读时绝不重新渲染");

        var preserved = store.Load(session).Sources["a.system"];
        preserved.BaselineText.Should().Be(baselineTextA);
        preserved.BaselineHash.Should().Be(baselineHashA, "基线文本未变，哈希也必须逐字节一致");
        preserved.BaselineValues!.ToJsonString().Should().Contain("\"a\"", "基线值不得被部分推进");
        preserved.CurrentValues!.ToJsonString().Should().Contain("\"b\"");
        preserved.PendingReanchor.Should().BeTrue("必须留下按源的重锚定重试标记");

        var countAfterUnavailable = session.MessageCount;

        // 源恢复可读（值仍是 B）：下一次装配重锚定到 B
        rendered = "## Fake B";
        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");

        var reanchored = await AssembleAsync(assembler, session);

        reanchored.Issues.Should().BeEmpty("重锚定是 epoch 变化，不是「渲染不确定」");
        reanchored.DeltaText.Should().BeNull("重锚定的源本轮不得再叙述增量");
        assembler.BuildSystemBaseline(session).Should().Be("## Fake B");

        var after = store.Load(session).Sources["a.system"];
        after.PendingReanchor.Should().BeFalse("重锚定成功后必须清除重试标记");
        after.BaselineText.Should().Be("## Fake B");
        after.BaselineValues!.ToJsonString().Should().Contain("\"b\"");
        after.BaselineHash.Should().NotBe(baselineHashA);

        // 之后同一状态再装配：不得重复叙述、不得再报问题、不得再插历史消息
        var stable = await AssembleAsync(assembler, session);

        stable.DeltaText.Should().BeNull();
        stable.Issues.Should().BeEmpty();
        session.MessageCount.Should().Be(countAfterUnavailable, "重锚定不得产生重复的叙述内容");
    }

    [Fact]
    public async Task MultipleMessageSources_WithDelta_ShouldInsertInDeterministicOrder()
    {
        // I4：多初始消息不得倒序；增量必须在初始消息之后、锚点之前
        var firstSource = new FakeSource
        {
            Key = new("a.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["1"]}""")
        };
        var secondSource = new FakeSource
        {
            Key = new("b.message"),
            Target = ContextSourceTarget.Message,
            Value = FakeSource.ValueOf("""{"items":["2"]}""")
        };
        var systemSource = new FakeSource { Key = new("c.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, firstSource, secondSource, systemSource);

        await AssembleAsync(assembler, session);
        session.MessageCount.Should().Be(2);
        session.Messages[0].Content.Should().Contain("\"1\"");
        session.Messages[1].Content.Should().Contain("\"2\"");

        // 模拟压缩 / revert：初始消息被移除，同时系统源发生变化（重放 + 增量同轮）
        var anchor = SessionMessage.UserMessage("user");
        session.AddMessage(anchor);
        foreach (var message in session.Messages.Where(m => !ReferenceEquals(m, anchor)).ToList())
            session.RemoveMessage(message);

        systemSource.Value = FakeSource.ValueOf("""{"items":["a","z"]}""");
        var result = await AssembleAsync(assembler, session, anchor.Id);

        result.ChangedSourceKeys.Should().HaveCount(3);
        session.Messages.Should().HaveCount(4);
        session.Messages[0].Content.Should().Contain("\"1\"");
        session.Messages[1].Content.Should().Contain("\"2\"");
        session.Messages[2].Content.Should().Contain("""<context-update source="c.system">""");
        session.Messages[3].Should().BeSameAs(anchor);
    }

    [Fact]
    public async Task ReadOnly_WithChangedSystemSource_ShouldReturnCurrentRender()
    {
        // I5：只读模式返回当前值渲染，而不是上次建立的旧基线；且不写状态
        var source = new FakeSource { Key = new("a.system") };
        var session = new SessionData { Id = "s1" };
        var assembler = CreateAssembler(session, source);
        await AssembleAsync(assembler, session);

        var storedBaseline = assembler.BuildSystemBaseline(session);
        storedBaseline.Should().Contain("""{"items":["a"]}""");

        source.Value = FakeSource.ValueOf("""{"items":["b"]}""");
        var readOnly = await AssembleAsync(assembler, session, mode: ContextAssemblyMode.ReadOnly);

        readOnly.SystemBaseline.Should().Contain("""{"items":["b"]}""");
        readOnly.SystemBaseline.Should().NotBe(storedBaseline);
        // 状态未被推进：已存基线仍是旧值（只读不写）
        assembler.BuildSystemBaseline(session).Should().Be(storedBaseline);
        session.MessageCount.Should().Be(0);
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

    /// <summary>本次读取附带的 Payload（用于验证「不落盘」）。</summary>
    public object? Payload { get; set; }

    /// <summary>自定义增量渲染；null 表示用默认文案。</summary>
    public Func<ContextSourceValue, ContextSourceValue, string?>? RenderBehavior { get; set; }

    /// <summary>自定义初始渲染（参数为调用序号）；null 表示用默认文案。</summary>
    public Func<int, string>? RenderInitialBehavior { get; set; }

    private int _renderInitialCount;

    /// <summary>为 true 时读取抛异常（验证异常映射为 Unavailable 并进入 Issues）。</summary>
    public bool ThrowOnRead { get; set; }

    public int ReadCount { get; private set; }

    public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        ReadCount++;
        if (ThrowOnRead)
            return Task.FromException<ContextSourceValue>(new IOException("读取失败"));

        var value = Value;
        return Task.FromResult(Payload is null
            ? value
            : new ContextSourceValue(value.State, value.Canonical, Payload));
    }

    public string RenderInitial(ContextSourceValue value) =>
        RenderInitialBehavior?.Invoke(_renderInitialCount++)
        ?? $"## Fake\n{CanonicalText(value)}";

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
