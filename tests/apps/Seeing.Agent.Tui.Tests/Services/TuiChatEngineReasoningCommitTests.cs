using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.Agent.Tui.Tests.Fakes;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 思考只出现一次且必须在正文之前：首个固化片携带推理，后续固化片与终态补写不带推理，
/// 活动区在 offset &gt; 0（正文已固化前缀）时也不再渲染推理。
/// </summary>
public sealed class TuiChatEngineReasoningCommitTests
{
    private const string Reasoning = "内部推理片段";
    private const string Text = "第一段\n\n第二段\n\n第三段";

    private static readonly TuiRenderer Renderer = new(new TuiRenderOptions());

    private static TuiBlock NewBlock() => new()
    {
        Key = "loop1_step1",
        Kind = TuiBlockKind.Assistant,
        Text = Text,
        Reasoning = Reasoning,
        IsStreaming = true,
    };

    [Fact]
    public async Task FirstPrefixCommit_ShouldCarryReasoningOnce()
    {
        var block = NewBlock();
        var cut = TuiChatEngine.FindStableCut(Text, 0);
        cut.Should().BeGreaterThan(0);

        var surface = new FakeTerminalSurface();
        var slice = TuiChatEngine.BuildPrefixCommitBlock(block, 0, cut);
        await surface.CommitAsync(Renderer.BuildCommitted(slice, 120), TestContext.Current.CancellationToken);

        surface.Committed.Should().ContainSingle();
        surface.Committed[0].Should().Contain(Reasoning);
        surface.Committed[0].Should().Contain("第一段");
    }

    [Fact]
    public async Task SubsequentPrefixAndTerminalCommit_ShouldNotCarryReasoning()
    {
        var block = NewBlock();
        var firstCut = TuiChatEngine.FindStableCut(Text, 0);
        firstCut.Should().BeGreaterThan(0);
        firstCut.Should().BeLessThan(Text.Length);

        var first = TuiChatEngine.BuildPrefixCommitBlock(block, 0, firstCut);
        var second = TuiChatEngine.BuildPrefixCommitBlock(block, firstCut, Text.Length);
        var terminal = TuiChatEngine.BuildTerminalCommitBlock(block, firstCut);

        var surface = new FakeTerminalSurface();
        await surface.CommitAsync(Renderer.BuildCommitted(first, 120), TestContext.Current.CancellationToken);
        await surface.CommitAsync(Renderer.BuildCommitted(second, 120), TestContext.Current.CancellationToken);
        await surface.CommitAsync(Renderer.BuildCommitted(terminal, 120), TestContext.Current.CancellationToken);

        surface.Committed[0].Should().Contain(Reasoning);
        surface.Committed[1].Should().NotContain(Reasoning);
        surface.Committed[1].Should().Contain("第三段");
        surface.Committed[2].Should().NotContain(Reasoning);
        surface.Committed[2].Should().Contain("第三段");
    }

    [Fact]
    public void ActiveView_AtOffsetZero_ShouldRenderReasoning()
    {
        var surface = new FakeTerminalSurface();
        var view = Renderer.BuildActiveView(NewState(), new TuiInputEditorState(), 100,
            committedOffsets: new Dictionary<string, int>());

        var text = surface.RenderToString(view);

        text.Should().Contain(Reasoning);
    }

    [Fact]
    public void ActiveView_AfterPrefixCommitted_ShouldNotRenderReasoning()
    {
        var surface = new FakeTerminalSurface();
        var offset = TuiChatEngine.FindStableCut(Text, 0);
        var view = Renderer.BuildActiveView(NewState(), new TuiInputEditorState(), 100,
            committedOffsets: new Dictionary<string, int> { ["loop1_step1"] = offset });

        var text = surface.RenderToString(view);

        text.Should().NotContain(Reasoning);
        text.Should().NotContain("第一段");
    }

    private static TuiViewState NewState()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build", IsExecuting = true };
        state.Upsert(NewBlock());
        return state;
    }
}
