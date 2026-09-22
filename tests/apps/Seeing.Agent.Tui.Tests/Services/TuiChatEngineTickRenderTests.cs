using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 周期 tick 重绘闸门回归：任一判据维度变化才重绘，全等则完全不动终端。
/// <para>
/// 背景：主循环每个 tick（60ms）都会尝试重绘，而空闲重绘会把终端宿主画在物理光标处的
/// 输入法组合串擦掉（输入中文时持续闪屏），故空闲时必须不写终端。
/// </para>
/// </summary>
public sealed class TuiChatEngineTickRenderTests
{
    private static TickRenderSnapshot Snapshot(
        long chars = 0,
        bool executing = false,
        int background = 0,
        int pending = 0,
        StatusSignature? status = null,
        int width = 120,
        int height = 40,
        int terminalCommits = 0)
        => new(chars, executing, background, pending, status ?? DefaultStatus, width, height, terminalCommits);

    private static readonly StatusSignature DefaultStatus = new(new TuiBudget(1200, 200000), false, @"D:\work");

    [Fact]
    public void ShouldRenderOnTick_WhenSnapshotUnchanged_ShouldNotRender()
        => TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot()).Should().BeFalse();

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void ShouldRenderOnTick_WhenStreamedCharsGrew_ShouldRender(long chars)
        => TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(chars: chars)).Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenExecuting_ShouldRender()
        => TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(executing: true)).Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenBackgroundExecutionRunning_ShouldRender()
        => TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(background: 2)).Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenPendingApprovalsChanged_ShouldRender()
        => TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(pending: 1)).Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenBudgetChanged_ShouldRender()
        => TuiChatEngine
            .ShouldRenderOnTick(Snapshot(), Snapshot(status: DefaultStatus with { Budget = new TuiBudget(2400, 200000) }))
            .Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenGlobalAutoApproveChanged_ShouldRender()
        => TuiChatEngine
            .ShouldRenderOnTick(Snapshot(), Snapshot(status: DefaultStatus with { GlobalAutoApprove = true }))
            .Should().BeTrue();

    [Fact]
    public void ShouldRenderOnTick_WhenTerminalResized_ShouldRender()
    {
        TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(width: 100)).Should().BeTrue();
        TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(height: 24)).Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderOnTick_WhenTerminalCommitted_ShouldRender()
    {
        // 固化提交会拆掉活动区（Live AutoClear 擦除整块）：代次变了必须立刻重绘，
        // 否则输入行与状态栏会一直空着（此时内容字符数可能完全没变）。
        TuiChatEngine.ShouldRenderOnTick(Snapshot(), Snapshot(terminalCommits: 1)).Should().BeTrue();
    }

    [Fact]
    public void StatusSignature_ShouldCompareByValue()
    {
        var a = new StatusSignature(new TuiBudget(1200, 200000), GlobalAutoApprove: true, WorkspaceRoot: @"D:\work");
        var b = new StatusSignature(new TuiBudget(1200, 200000), GlobalAutoApprove: true, WorkspaceRoot: @"D:\work");

        a.Should().Be(b);
        a.Should().NotBe(b with { GlobalAutoApprove = false });
        a.Should().NotBe(new StatusSignature(null, true, @"D:\work"));
    }
}
