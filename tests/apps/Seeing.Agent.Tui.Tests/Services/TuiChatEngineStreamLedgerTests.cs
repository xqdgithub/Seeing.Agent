using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 流式固化账本语义：
/// <list type="bullet">
/// <item>stream.start 重置该块账本（重试不重复、不丢字）；</item>
/// <item>按已固化长度增量提交（不重不漏）；</item>
/// <item>提交失败不推进账本（保持可重试）；</item>
/// <item>迟到的推理单独补写。</item>
/// </list>
/// </summary>
public sealed class TuiChatEngineStreamLedgerTests
{
    private static TuiBlock Assistant(string key, string text, string reasoning = "") => new()
    {
        Key = key,
        Kind = TuiBlockKind.Assistant,
        Text = text,
        Reasoning = reasoning,
        IsTerminal = true,
    };

    [Fact]
    public void StreamStartKey_ShouldMatchInterpreterKeyRule()
    {
        var key = TuiChatEngine.StreamStartKey(new StreamStartEvent { SessionId = "s", LoopId = "loop1", Step = 2 });

        key.Should().Be("loop1_step2");
        key.Should().Be(TuiViewState.AssistantKey("loop1", 2, "step2"));
    }

    [Fact]
    public void StreamStartKey_EmptyLoopId_ShouldMatchInterpreterFallback()
    {
        var key = TuiChatEngine.StreamStartKey(new StreamStartEvent { SessionId = "s", LoopId = null, Step = 3 });

        key.Should().Be(TuiViewState.AssistantKey(null, 3, "step3"));
    }

    [Fact]
    public void Reset_ShouldClearAllLedgerEntriesForKey()
    {
        var ledger = new TuiCommitLedger();
        ledger.Advance("k", 10, reasoningCommitted: true);

        ledger.Reset("k");

        ledger.GetCommittedChars("k", 10).Should().Be(0);
        ledger.IsCommitted("k").Should().BeFalse();
        ledger.IsReasoningCommitted("k").Should().BeFalse();
    }

    [Fact]
    public void Plan_AfterReset_ShouldReCommitEntireText()
    {
        // StreamStart 重置后已固化偏移归零：从 0 提交全文，既不重复也不丢字。
        var plan = TuiChatEngine.PlanAssistantTerminalCommit(
            textLength: 30, committedChars: 0, hasReasoning: false, reasoningCommitted: false);

        plan.TextOffset.Should().Be(0);
        plan.TextLength.Should().Be(30);
        plan.CommitReasoning.Should().BeFalse();
    }

    [Fact]
    public void Plan_PartialCommitted_ShouldOnlyCommitDelta()
    {
        var plan = TuiChatEngine.PlanAssistantTerminalCommit(
            textLength: 30, committedChars: 12, hasReasoning: false, reasoningCommitted: false);

        plan.TextOffset.Should().Be(12);
        plan.TextLength.Should().Be(18);
    }

    [Fact]
    public void Plan_LateReasoning_ShouldFlagReasoningCommit()
    {
        var plan = TuiChatEngine.PlanAssistantTerminalCommit(
            textLength: 30, committedChars: 30, hasReasoning: true, reasoningCommitted: false);

        plan.TextLength.Should().Be(0);
        plan.CommitReasoning.Should().BeTrue();
    }

    [Fact]
    public void Plan_FullyCommitted_ShouldBeNoOp()
    {
        var plan = TuiChatEngine.PlanAssistantTerminalCommit(
            textLength: 30, committedChars: 30, hasReasoning: true, reasoningCommitted: true);

        plan.TextLength.Should().Be(0);
        plan.CommitReasoning.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_FirstTime_ShouldCommitTextAndAdvanceLedger()
    {
        var ledger = new TuiCommitLedger();
        var block = Assistant("k", "hello");
        var commits = new List<TuiBlock>();

        var ok = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, b => { commits.Add(b); return Task.FromResult(true); });

        ok.Should().BeTrue();
        commits.Should().ContainSingle();
        commits[0].Text.Should().Be("hello");
        commits[0].Reasoning.Should().BeEmpty();
        ledger.GetCommittedChars("k", block.Text.Length).Should().Be(5);
    }

    [Fact]
    public async Task Commit_IncrementalGrowth_ShouldCommitOnlyDelta()
    {
        var ledger = new TuiCommitLedger();
        ledger.Advance("k", 5, reasoningCommitted: false);
        var block = Assistant("k", "hello world");
        TuiBlock? committed = null;

        var ok = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, b => { committed = b; return Task.FromResult(true); });

        ok.Should().BeTrue();
        committed!.Text.Should().Be(" world");
        committed.Reasoning.Should().BeEmpty();
        ledger.GetCommittedChars("k", block.Text.Length).Should().Be(11);
    }

    [Fact]
    public async Task Commit_Failure_ShouldNotAdvanceLedger()
    {
        var ledger = new TuiCommitLedger();
        var block = Assistant("k", "hello");

        var ok = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, _ => Task.FromResult(false));

        ok.Should().BeFalse();
        ledger.GetCommittedChars("k", block.Text.Length).Should().Be(0);
        ledger.IsCommitted("k").Should().BeFalse();
    }

    [Fact]
    public async Task Commit_LateReasoning_ShouldWriteReasoningOnlyBlock()
    {
        var ledger = new TuiCommitLedger();
        ledger.Advance("k", 5, reasoningCommitted: false);   // 正文已固化，推理尚未
        var block = Assistant("k", "hello", reasoning: "因为所以");
        var commits = new List<TuiBlock>();

        var ok = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, b => { commits.Add(b); return Task.FromResult(true); });

        ok.Should().BeTrue();
        commits.Should().ContainSingle();
        commits[0].Text.Should().BeEmpty();
        commits[0].Reasoning.Should().Be("因为所以");
        ledger.IsReasoningCommitted("k").Should().BeTrue();
    }

    [Fact]
    public async Task Commit_ReasoningSucceedsTextFails_ShouldAdvanceReasoningOnly()
    {
        var ledger = new TuiCommitLedger();
        var block = Assistant("k", "hello", reasoning: "r");

        var ok = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, b => Task.FromResult(string.IsNullOrEmpty(b.Text)));

        ok.Should().BeTrue();
        ledger.IsReasoningCommitted("k").Should().BeTrue();
        ledger.GetCommittedChars("k", block.Text.Length).Should().Be(0);
    }

    // 事件源重建/事件回放：账本刻意不清空，回放同样的内容必须不再写入（锁定“不重复”假设）。
    [Fact]
    public async Task Commit_ReplayedIdenticalBlockAfterFullCommit_ShouldNotWriteAgain()
    {
        var ledger = new TuiCommitLedger();
        var block = Assistant("k", "hello", reasoning: "r");

        await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, _ => Task.FromResult(true));

        var commits = new List<TuiBlock>();
        var second = await TuiChatEngine.CommitAssistantTerminalAsync(
            ledger, block, b => { commits.Add(b); return Task.FromResult(true); });

        second.Should().BeFalse("账本已记满时无待提交内容，不算一次提交");
        commits.Should().BeEmpty("同一内容在账本已记满时不得重复写入滚动历史");
    }

    // 文本变短（如流式重试后重置）时，陈旧偏移必须失效并整段重写，避免切片越界丢字。
    [Fact]
    public void Plan_StaleOffsetBeyondText_ShouldRewriteWholeText()
    {
        var plan = TuiChatEngine.PlanAssistantTerminalCommit(
            textLength: 10, committedChars: 30, hasReasoning: false, reasoningCommitted: true);

        plan.TextOffset.Should().Be(0);
        plan.TextLength.Should().Be(10);
    }
}
