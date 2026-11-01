using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// 记忆注入的**累积行为**验收（T9 三轮补丁原始 bug 的回归守门）。
/// <para>
/// 原始 bug 形态：<c>LlmService</c> 原地改写 <c>request.SystemPrompt</c> 注入记忆，
/// 重试 / 重复装配时记忆块被**重复追加**。现方案把记忆迁到 <see cref="MemoryContextSource"/>
/// （<c>Target = Message</c>），由 <see cref="IContextAssembler"/> 每轮装配一次：
/// 值不变 → 不新增消息；值变化 → 恰好新增一条。
/// </para>
/// <para>
/// 必须用 <see cref="ContextAssemblyMode.Persist"/>：<see cref="ContextAssemblyMode.ReadOnly"/>
/// 下 <c>DeltaText</c> 恒为 null、也不写状态，对「是否累积」没有判别力。
/// </para>
/// <para>
/// 直接 <c>new ContextAssembler(...)</c> 而不走 DI：DI 解析会掩盖「源未真正接入装配管道」
/// 这类失败，测试就失去守门意义。
/// </para>
/// </summary>
public class MemoryContextSourceAccumulationTests
{
    /// <summary>
    /// Review Focus #4：同一值（同一批命中）重复装配不得新增任何消息。
    /// <para>
    /// <c>Target = Message</c> 的语义：记忆内容进历史消息、**不进系统提示**，
    /// 因此 <c>SystemBaseline</c> 为空、首轮内容落在 <c>HistoryBaseline</c>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Repeated_Assembly_With_Same_Hits_Should_Not_Add_Message()
    {
        var harness = BuildWithMemorySource(
            hits: [Hit("a.md", 0.9, "标题A", "内容A")]);

        var first = await AssembleAsync(harness);
        var before = harness.Session.MessageCount;

        var second = await AssembleAsync(harness);

        // Message 落点：内容以消息形式进入历史，系统提示里不含记忆
        first.SystemBaseline.Should().BeEmpty("Message 落点的基线属于历史，不进入系统提示词");
        first.SystemBaseline.Should().NotContain(MemoryContextSource.Heading);
        first.HistoryBaseline.Should().Contain(MemoryContextSource.Heading);
        first.HistoryBaseline.Should().Contain("标题A");

        harness.Session.MessageCount.Should().Be(before, "值未变不应新增消息");
        second.DeltaText.Should().BeNull("值未变不应产生增量叙述");
        second.HistoryBaseline.Should().BeNullOrEmpty("初始基线消息仍存活，不应重放");

        // 判别力：两次装配都必须真的读过源（否则「无新消息」可能只是源没接上）
        harness.Recall.CallCount.Should().Be(2, "两次装配都必须实际调用召回");
    }

    /// <summary>
    /// 值变化时恰好新增一条增量消息；后续再装配同值不得继续累积。
    /// </summary>
    [Fact]
    public async Task Changed_Hits_Should_Add_Exactly_One_Message()
    {
        var harness = BuildWithMemorySource(hits: [Hit("a.md", 0.9)]);
        await AssembleAsync(harness);
        var before = harness.Session.MessageCount;

        harness.Recall.SetHits([Hit("b.md", 0.7)]);
        var second = await AssembleAsync(harness);

        harness.Session.MessageCount.Should().Be(before + 1, "值变化应恰好新增一条");
        second.DeltaText.Should().NotBeNull("值变化必须叙述");
        second.DeltaText.Should().Contain("b.md", "新增命中必须被叙述");

        // 再装配一次同值：增量消息不得继续追加（累积的直接守门）
        var third = await AssembleAsync(harness);
        harness.Session.MessageCount.Should().Be(before + 1, "同值重复装配不得继续累积");
        third.DeltaText.Should().BeNull();
        harness.Recall.CallCount.Should().Be(3, "三次装配都必须实际调用召回");
    }

    // ===== 辅助 =====

    private static async Task<ContextAssemblyResult> AssembleAsync(Harness harness) =>
        // Persist：ReadOnly 下 DeltaText 恒 null、不写状态，无法判别累积
        await harness.Assembler.EnsureFreshAsync(
            harness.Session,
            ContextAssemblyMode.Persist,
            insertBeforeMessageId: null,
            skip: null,
            TestContext.Current.CancellationToken);

    /// <summary>装配器 + 会话 + 召回桩（便于断言「源确实被读」与切换命中）。</summary>
    private sealed record Harness(
        ContextAssembler Assembler,
        SessionData Session,
        RecordingRecall Recall);

    private static Harness BuildWithMemorySource(IReadOnlyList<SearchHit> hits)
    {
        var session = new SessionData { Id = "ses_memory_accumulation" };
        var recall = new RecordingRecall(hits);

        var source = new MemoryContextSource(
            new FakeExecutionScope { SessionId = session.Id, UserQuery = "本轮问题" },
            recall,
            MemoryTestOptions.Monitor(new MemoryOptions()),
            new MemoryModuleActivity());

        var manager = new Mock<ISessionManager>();
        manager
            .Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        // 源列表里确实只有这个记忆源（不接别的源，避免「无变化」被其它源带出来）
        var assembler = new ContextAssembler(
            new IContextSource[] { source },
            manager.Object,
            NullLogger<ContextAssembler>.Instance);

        return new Harness(assembler, session, recall);
    }

    private static SearchHit Hit(string path, double score, string? title = null, string? content = null) =>
        new(
            FileNode.Create(path, content ?? "正文", FileMetadata.Create("id", MemoryType.Daily, title)),
            score,
            score,
            0);

    /// <summary>可控召回桩：记录调用次数，支持中途切换命中集。</summary>
    private sealed class RecordingRecall : IMemoryRecallService
    {
        private IReadOnlyList<SearchHit> _hits;

        public RecordingRecall(IReadOnlyList<SearchHit> hits) => _hits = hits;

        public int CallCount { get; private set; }

        public void SetHits(IReadOnlyList<SearchHit> hits) => _hits = hits;

        public Task<IReadOnlyList<SearchHit>> RecallAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_hits);
        }
    }

    private sealed class FakeExecutionScope : IContextExecutionScope
    {
        public string SessionId { get; set; } = string.Empty;

        public string Cwd { get; set; } = string.Empty;

        public string WorkspaceRoot { get; set; } = string.Empty;

        public SkillCatalogSnapshot? SkillSnapshot { get; set; }

        public string? UserQuery { get; set; }
    }
}
