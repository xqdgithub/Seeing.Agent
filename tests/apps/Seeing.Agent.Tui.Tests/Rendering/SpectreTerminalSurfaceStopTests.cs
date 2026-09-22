using FluentAssertions;
using Seeing.Agent.Tui.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 停机路径的纯逻辑断言：<b>不</b>启动真实渲染线程、<b>不</b>等待按键或终端，避免测试卡死。
/// 渲染线程相关的行为以抽出的 <c>internal static</c> 纯函数间接覆盖。
/// </summary>
public sealed class SpectreTerminalSurfaceStopTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldSkipPrompt_ShouldMirrorStopRequested(bool stopRequested)
        => SpectreTerminalSurface.ShouldSkipPrompt(stopRequested).Should().Be(stopRequested);

    [Fact]
    public void TryMarkRestored_ShouldBeIdempotent()
    {
        var flag = 0;

        SpectreTerminalSurface.TryMarkRestored(ref flag).Should().BeTrue();
        SpectreTerminalSurface.TryMarkRestored(ref flag).Should().BeFalse();
        SpectreTerminalSurface.TryMarkRestored(ref flag).Should().BeFalse();
        flag.Should().Be(1);
    }

    [Fact]
    public void TerminalRestoreSequence_ShouldOnlyRestoreCursorVisibility()
    {
        // 分工：?2004l（关闭 bracketed paste）由 RawInputReader.RestoreConsole 负责；
        // 终端出口只写光标可见序列，不得重复写出 2004，避免两处重复。
        SpectreTerminalSurface.TerminalRestoreSequence.Should().Be("\u001b[?25h");
        SpectreTerminalSurface.TerminalRestoreSequence.Should().NotContain("2004");
    }
}
