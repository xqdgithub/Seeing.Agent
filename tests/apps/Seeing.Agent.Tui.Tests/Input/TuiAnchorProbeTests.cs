using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

/// <summary>
/// <see cref="TuiAnchorProbe"/> 契约回归：发一个收一个、代次配对、未校准 false（spec §5）。
/// </summary>
public sealed class TuiAnchorProbeTests
{
    [Fact]
    public void TryGet_Initial_ShouldBeFalse()
    {
        var probe = new TuiAnchorProbe();

        probe.TryGet(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Report_WithoutPending_ShouldBeIgnored()
    {
        var probe = new TuiAnchorProbe();

        probe.Report(8);

        probe.TryGet(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void BeginProbe_ShouldIncrementGenAndCallWriterOnce()
    {
        var probe = new TuiAnchorProbe();
        var writes = 0;

        var g1 = probe.BeginProbe(() => writes++);
        var g2 = probe.BeginProbe(() => writes++);

        g1.Should().Be(1);
        g2.Should().Be(2);
        writes.Should().Be(2);
    }

    [Fact]
    public void BeginProbe_NullWriter_ShouldThrow()
    {
        var probe = new TuiAnchorProbe();

        var act = () => probe.BeginProbe(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Report_AfterBegin_ShouldPairRowWithPendingGen()
    {
        var probe = new TuiAnchorProbe();
        var gen = probe.BeginProbe(() => { });

        probe.Report(12);

        probe.TryGet(out var row, out var gotGen).Should().BeTrue();
        row.Should().Be(12);
        gotGen.Should().Be(gen);
    }

    [Fact]
    public void Report_SecondReplyWithoutNewProbe_ShouldBeIgnored()
    {
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(() => { });
        probe.Report(12);

        probe.Report(99);

        probe.TryGet(out var row, out _).Should().BeTrue();
        row.Should().Be(12);
    }

    [Fact]
    public void BeginProbe_TwiceBeforeReply_ReportsPairInFifoOrder()
    {
        var probe = new TuiAnchorProbe();
        var gen1 = probe.BeginProbe(() => { });
        var gen2 = probe.BeginProbe(() => { });

        // 终端按序回复：第一回复配对最早在途请求 gen1，第二回复配对 gen2。
        probe.Report(10);
        probe.TryGet(out var r1, out var g1).Should().BeTrue();
        r1.Should().Be(10);
        g1.Should().Be(gen1);

        probe.Report(20);
        probe.TryGet(out var r2, out var g2).Should().BeTrue();
        r2.Should().Be(20);
        g2.Should().Be(gen2);
    }

    [Fact]
    public void Report_WhenNoPending_ShouldBeIgnored()
    {
        var probe = new TuiAnchorProbe();

        probe.Report(99);

        probe.TryGet(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_AfterCalibrated_ShouldMakeTryGetFalse()
    {
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(() => { });
        probe.Report(30);

        probe.Invalidate();

        probe.TryGet(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_DuringPending_LateReply_ShouldBeDropped()
    {
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(() => { });

        probe.Invalidate();
        probe.Report(30);

        probe.TryGet(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void BeginProbe_AfterInvalidate_ReportShouldRecalibrate()
    {
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(() => { });
        probe.Report(10);
        probe.Invalidate();

        var gen = probe.BeginProbe(() => { });
        probe.Report(44);

        probe.TryGet(out var row, out var gotGen).Should().BeTrue();
        row.Should().Be(44);
        gotGen.Should().Be(gen);
    }

    [Fact]
    public void NullProbe_TryGet_ShouldBeFalseAndBeginShouldNotWrite()
    {
        var probe = new NullTuiAnchorProbe();
        var writes = 0;

        probe.BeginProbe(() => writes++).Should().Be(0);
        probe.Report(5);

        probe.TryGet(out _, out _).Should().BeFalse();
        writes.Should().Be(0);
    }
}
