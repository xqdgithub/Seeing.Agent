using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// <see cref="TuiHitTest.TryResolve"/> 纯函数回归：同代次命中、跨代次/未校准失效、行换算（spec §6.4/§7.1）。
/// </summary>
public sealed class TuiHitTestTests
{
    private const long FrameGen = 7;

    [Fact]
    public void TryResolve_ProbeNotCalibrated_ShouldReturnNull()
    {
        var probe = new TuiAnchorProbe();
        var hits = new[] { new TuiHitRegion(0, 0, FrameGen) };

        TuiHitTest.TryResolve(probe, hits, FrameGen, absRow: 10).Should().BeNull();
    }

    [Fact]
    public void TryResolve_GenMismatch_ShouldReturnNull()
    {
        var hits = new[] { new TuiHitRegion(1, 1, FrameGen) };

        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen + 1), hits, FrameGen, absRow: 9).Should().BeNull();
    }

    [Fact]
    public void TryResolve_SameGen_RowMatch_ShouldReturnItemIndex()
    {
        // 底锚 = 末行 10；item0 距末行 2（行 8）、item1 距末行 1（行 9）、item2 末行（行 10）。
        var hits = new[]
        {
            new TuiHitRegion(0, 2, FrameGen),
            new TuiHitRegion(1, 1, FrameGen),
            new TuiHitRegion(2, 0, FrameGen),
        };

        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen), hits, FrameGen, absRow: 8).Should().Be(0);
        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen), hits, FrameGen, absRow: 9).Should().Be(1);
        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen), hits, FrameGen, absRow: 10).Should().Be(2);
    }

    [Fact]
    public void TryResolve_SameGen_NoRowMatch_ShouldReturnNull()
    {
        var hits = new[] { new TuiHitRegion(0, 2, FrameGen) };

        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen), hits, FrameGen, absRow: 4).Should().BeNull();
    }

    [Fact]
    public void TryResolve_EmptyHits_ShouldReturnNull()
    {
        TuiHitTest.TryResolve(new FakeProbe(row: 10, gen: FrameGen), [], FrameGen, absRow: 10).Should().BeNull();
    }

    [Fact]
    public void TryResolve_NullProbe_ShouldReturnNull()
    {
        var hits = new[] { new TuiHitRegion(0, 0, FrameGen) };

        TuiHitTest.TryResolve(null!, hits, FrameGen, absRow: 10).Should().BeNull();
    }

    private sealed class FakeProbe(int row, long gen) : ITuiAnchorProbe
    {
        public void Report(int row)
        {
        }

        public bool TryGet(out int r, out long g)
        {
            r = row;
            g = gen;
            return true;
        }

        public void Invalidate()
        {
        }

        public long BeginProbe(Action writeDsr) => gen;

        public void BeginProbe(long g, Action writeDsr) => writeDsr();
    }
}
