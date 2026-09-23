using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

public class RawInputReaderMouseTests
{
    private static readonly byte[] SgrPress = "\x1b[<0;10;5M"u8.ToArray();
    private static readonly byte[] DsrReport = "\x1b[8;20R"u8.ToArray();

    private sealed class FakeAnchorProbe : ITuiAnchorProbe
    {
        public List<int> ReportedRows { get; } = [];

        public void Report(int row) => ReportedRows.Add(row);

        public bool TryGet(out int row, out long gen)
        {
            row = 0;
            gen = 0;
            return false;
        }

        public void Invalidate() { }

        public long BeginProbe(Action writeDsr) => 0;

        public void BeginProbe(long gen, Action writeDsr) => writeDsr();
    }

    private static TuiRawInput[] Feed(RawInputReader reader, byte[] bytes)
    {
        reader.AppendAndDrain(bytes, bytes.Length);

        var results = new List<TuiRawInput>();
        while (reader.Reader.TryRead(out var item))
            results.Add(item);
        return results.ToArray();
    }

    private static TuiRawInput[] Feed(string raw, bool mouseEnabled = true, ITuiAnchorProbe? probe = null)
        => Feed(new RawInputReader(mouseEnabled, probe), System.Text.Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Drain_SgrMousePress_ShouldEmitLeftPressWithCoordinates()
    {
        // Arrange
        var reader = new RawInputReader(mouseEnabled: true);

        // Act
        var items = Feed(reader, SgrPress);

        // Assert
        items.Should().Equal(new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Press, 10, 5));
    }

    [Fact]
    public void Drain_SgrMouseRelease_ShouldEmitReleasePhase()
    {
        var items = Feed("\x1b[<0;10;5m");

        items.Should().Equal(new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Release, 10, 5));
    }

    [Fact]
    public void Drain_SgrMouseMove_ShouldEmitMotionPhaseWithPressedButton()
    {
        var items = Feed("\x1b[<32;10;5M");

        items.Should().Equal(new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Motion, 10, 5));
    }

    [Fact]
    public void Drain_SgrMouseMoveNoButton_ShouldMapButtonNone()
    {
        var items = Feed("\x1b[<35;4;6M");

        items.Should().Equal(new TuiRawMouse(TuiMouseButton.None, TuiMousePhase.Motion, 4, 6));
    }

    [Fact]
    public void Drain_SgrMouseWheelUpAndDown_ShouldMapWheelButtonsAsPress()
    {
        var items = Feed("\x1b[<64;7;3M\x1b[<65;7;3M");

        items.Should().Equal(
            new TuiRawMouse(TuiMouseButton.WheelUp, TuiMousePhase.Press, 7, 3),
            new TuiRawMouse(TuiMouseButton.WheelDown, TuiMousePhase.Press, 7, 3));
    }

    [Fact]
    public void Drain_SgrMouseMiddleAndRight_ShouldMapButtons()
    {
        var items = Feed("\x1b[<1;2;3M\x1b[<2;2;3M");

        items.Should().Equal(
            new TuiRawMouse(TuiMouseButton.Middle, TuiMousePhase.Press, 2, 3),
            new TuiRawMouse(TuiMouseButton.Right, TuiMousePhase.Press, 2, 3));
    }

    [Fact]
    public void Drain_SgrMouseSplitAcrossReads_ShouldEmitOnlyAfterFinalByte()
    {
        // Arrange
        var reader = new RawInputReader(mouseEnabled: true);

        // Act：前缀（无终字节）先到 —— 不产任何输入；终字节 'M' 到达后才产出。
        var partial = Feed(reader, "\x1b[<0;10;5"u8.ToArray());
        var final = Feed(reader, "M"u8.ToArray());

        // Assert
        partial.Should().BeEmpty();
        final.Should().Equal(new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Press, 10, 5));
    }

    [Fact]
    public void Drain_SgrMouseUnparsableParams_ShouldDiscardWithoutProducingInput()
    {
        // 段数不足/多余均属畸形 SGR 鼠标序列：整体（含终字节）丢弃，绝不落 InsertText/Escape。
        Feed("\x1b[<1;2M").Should().BeEmpty();
        Feed("\x1b[<1;2;3;4M").Should().BeEmpty();
        Feed("\x1b[<;1;1M").Should().BeEmpty();
    }

    [Fact]
    public void Drain_DsrCursorReport_ShouldRouteToProbeAndNotEmitInput()
    {
        // Arrange
        var probe = new FakeAnchorProbe();
        var reader = new RawInputReader(mouseEnabled: true, probe);

        // Act
        var items = Feed(reader, DsrReport);

        // Assert：DSR 旁路直达探针，通道零输出（不回灌按键流）。
        probe.ReportedRows.Should().Equal(8);
        items.Should().BeEmpty();
    }

    [Fact]
    public void Drain_DsrWithoutProbe_ShouldSilentlyConsume()
    {
        var items = Feed("\x1b[8;20R", probe: null);

        items.Should().BeEmpty();
    }

    [Fact]
    public void Drain_MousePasteAndRegularCsiMixed_ShouldNotMisclassify()
    {
        // Arrange：粘贴（起止序列+文本）→ 鼠标 press → 普通 CSI（上箭头）→ DSR 混排。
        // 粘贴单独一批：粘贴结束的 DrainLocked 提前返回为既有语义（残字节留待下一批），与鼠标解析正交。
        var probe = new FakeAnchorProbe();
        var reader = new RawInputReader(mouseEnabled: true, probe);
        var items = new List<TuiRawInput>();
        items.AddRange(Feed(reader, System.Text.Encoding.UTF8.GetBytes("\x1b[200~hi\x1b[201~")));
        items.AddRange(Feed(reader, System.Text.Encoding.UTF8.GetBytes("\x1b[<0;1;1M\x1b[A\x1b[9;99R")));

        // Assert：粘贴边界、鼠标、方向键转义各自归位；DSR 仅旁路探针不出现在通道。
        probe.ReportedRows.Should().Equal(9);
        items.Should().Equal(
            new TuiRawPasteStart(),
            new TuiRawText("hi"),
            new TuiRawPasteEnd(),
            new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Press, 1, 1),
            new TuiRawEscape("\x1b[A"));
    }
}
