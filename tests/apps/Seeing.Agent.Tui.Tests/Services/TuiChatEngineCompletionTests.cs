using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 内联补全下拉的引擎侧决策回归（spec §6）：优先路由、接受带入切分、帧代次分配、resize 失效、选择态脏检查签名。
/// <para>均为 <see cref="TuiChatEngine"/> 的 internal static 纯函数，脱离终端/事件泵单测。</para>
/// </summary>
public sealed class TuiChatEngineCompletionTests
{
    // —— §6.2 优先路由决策 ——

    [Theory]
    [InlineData(TuiInputAction.HistoryPrev, TuiChatEngine.CompletionRouteAction.MoveUp)]
    [InlineData(TuiInputAction.HistoryNext, TuiChatEngine.CompletionRouteAction.MoveDown)]
    [InlineData(TuiInputAction.Submit, TuiChatEngine.CompletionRouteAction.Accept)]
    [InlineData(TuiInputAction.Complete, TuiChatEngine.CompletionRouteAction.Accept)]
    [InlineData(TuiInputAction.Cancel, TuiChatEngine.CompletionRouteAction.Dismiss)]
    [InlineData(TuiInputAction.Mouse, TuiChatEngine.CompletionRouteAction.MouseHandle)]
    [InlineData(TuiInputAction.InsertText, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.Backspace, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.MoveLeft, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.None, TuiChatEngine.CompletionRouteAction.PassThrough)]
    public void DecideCompletionRoute_DropdownVisible_ShouldRoute(
        TuiInputAction action,
        TuiChatEngine.CompletionRouteAction expected)
    {
        // Act
        var route = TuiChatEngine.DecideCompletionRoute(dropdownVisible: true, action);

        // Assert
        route.Should().Be(expected);
    }

    [Theory]
    [InlineData(TuiInputAction.Mouse, TuiChatEngine.CompletionRouteAction.MouseIgnore)]
    [InlineData(TuiInputAction.Submit, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.Cancel, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.HistoryPrev, TuiChatEngine.CompletionRouteAction.PassThrough)]
    [InlineData(TuiInputAction.Complete, TuiChatEngine.CompletionRouteAction.PassThrough)]
    public void DecideCompletionRoute_DropdownHidden_ShouldNotIntercept(
        TuiInputAction action,
        TuiChatEngine.CompletionRouteAction expected)
    {
        // Act
        var route = TuiChatEngine.DecideCompletionRoute(dropdownVisible: false, action);

        // Assert
        route.Should().Be(expected);
    }

    [Fact]
    public void DecideCompletionRoute_DropdownHidden_Mouse_ShouldBeNoOp()
    {
        // 无下拉的鼠标必须走 MouseIgnore（不 Apply、不重置 _cancelArmedAt、不设 _lastRenderAt）。
        TuiChatEngine
            .DecideCompletionRoute(dropdownVisible: false, TuiInputAction.Mouse)
            .Should().Be(TuiChatEngine.CompletionRouteAction.MouseIgnore);
    }

    // —— §6.3 接受带入 token 切分 ——

    [Fact]
    public void TryComputeAccept_PartialToken_ShouldReplaceAndPlaceCursorAfterSpace()
    {
        // Arrange
        var ok = TuiChatEngine.TryComputeAccept("/mo", cursor: 3, "/model", out var text, out var cursor);

        // Assert：/mo → "/model "，光标落在空格后（index 7）。
        ok.Should().BeTrue();
        text.Should().Be("/model ");
        cursor.Should().Be(7);
    }

    [Fact]
    public void TryComputeAccept_CompleteToken_ShouldAppendTrailingSpace()
    {
        var ok = TuiChatEngine.TryComputeAccept("/model", cursor: 6, "/model", out var text, out var cursor);

        ok.Should().BeTrue();
        text.Should().Be("/model ");
        cursor.Should().Be(7);
    }

    [Fact]
    public void TryComputeAccept_TokenWithArgsAfter_ShouldPreserveArgs()
    {
        // 光标停在 token 内，token 后已有参数：替换命令名、保留参数、单空格分隔。
        var ok = TuiChatEngine.TryComputeAccept("/mo abc", cursor: 3, "/model", out var text, out var cursor);

        ok.Should().BeTrue();
        text.Should().Be("/model abc");
        cursor.Should().Be(7);
    }

    [Fact]
    public void TryComputeAccept_LeadingWhitespace_ShouldPreservePrefix()
    {
        var ok = TuiChatEngine.TryComputeAccept("  /mo", cursor: 5, "/model", out var text, out var cursor);

        ok.Should().BeTrue();
        text.Should().Be("  /model ");
        cursor.Should().Be(9);
    }

    [Theory]
    [InlineData("hello", 5)]
    [InlineData("", 0)]
    [InlineData("say /hi", 7)]
    public void TryComputeAccept_NotSlashToken_ShouldReturnFalse(string text, int cursor)
    {
        // Act
        var ok = TuiChatEngine.TryComputeAccept(text, cursor, "/model", out var newText, out _);

        // Assert
        ok.Should().BeFalse();
        newText.Should().Be(text);
    }

    // —— §6.4 帧代次分配（DSR 探测门控） ——

    [Fact]
    public void ComputeCompletionProbeGen_HasCandidatesAndMouse_ShouldIncrementNonZero()
    {
        // Arrange / Act
        var (probeGen, next) = TuiChatEngine.ComputeCompletionProbeGen(counter: 4, hasCandidates: true, mouseEnabled: true);

        // Assert
        probeGen.Should().Be(5);
        next.Should().Be(5);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ComputeCompletionProbeGen_NoProbe_ShouldReturnZeroAndPreserveCounter(bool hasCandidates, bool mouseEnabled)
    {
        // 无候选或无鼠标：本帧不探测（probeGen=0），且计数器保持（单调不复用，避免归 0 后复升旧 gen 与底锚错配）。
        var (probeGen, next) = TuiChatEngine.ComputeCompletionProbeGen(7, hasCandidates, mouseEnabled);

        probeGen.Should().Be(0);
        next.Should().Be(7);
    }

    [Fact]
    public void ComputeCompletionProbeGen_ConsecutiveProbeFrames_ShouldBeMonotonic()
    {
        var counter = 0L;
        var gens = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var (probeGen, next) = TuiChatEngine.ComputeCompletionProbeGen(counter, hasCandidates: true, mouseEnabled: true);
            gens.Add(probeGen);
            counter = next;
        }

        gens.Should().Equal(1, 2, 3);
    }

    // —— §6.4 resize 失效底锚 ——

    [Theory]
    [InlineData(120, 40, 120, 40, false)]
    [InlineData(120, 40, 100, 40, true)]
    [InlineData(120, 40, 120, 30, true)]
    [InlineData(0, 0, 120, 40, false)]
    public void ShouldInvalidateAnchorOnResize_ShouldCompareDimensions(
        int lastW, int lastH, int w, int h, bool expected)
    {
        TuiChatEngine.ShouldInvalidateAnchorOnResize(lastW, lastH, w, h).Should().Be(expected);
    }

    // —— §6.5 选择态入脏检查 ——

    [Fact]
    public void CompletionSignatureOf_SameState_ShouldBeEqual()
    {
        var candidates = Items("/model", "/new");

        TuiChatEngine.CompletionSignatureOf(candidates, 1, visible: true)
            .Should().Be(TuiChatEngine.CompletionSignatureOf(Items("/model", "/new"), 1, visible: true));
    }

    [Fact]
    public void CompletionSignatureOf_CursorChanged_ShouldDiffer()
    {
        var candidates = Items("/model", "/new");

        TuiChatEngine.CompletionSignatureOf(candidates, 0, visible: true)
            .Should().NotBe(TuiChatEngine.CompletionSignatureOf(candidates, 1, visible: true));
    }

    [Fact]
    public void CompletionSignatureOf_CandidateSetChanged_ShouldDiffer()
    {
        TuiChatEngine.CompletionSignatureOf(Items("/model"), 0, visible: true)
            .Should().NotBe(TuiChatEngine.CompletionSignatureOf(Items("/new"), 0, visible: true));
    }

    [Fact]
    public void CompletionSignatureOf_Hidden_ShouldBeEmpty()
    {
        TuiChatEngine.CompletionSignatureOf(Items("/model"), 0, visible: false).Should().BeEmpty();
        TuiChatEngine.CompletionSignatureOf([], 0, visible: true).Should().BeEmpty();
    }

    private static IReadOnlyList<TuiCompletionItem> Items(params string[] names)
        => names.Select(n => new TuiCompletionItem(n, "")).ToList();
}
