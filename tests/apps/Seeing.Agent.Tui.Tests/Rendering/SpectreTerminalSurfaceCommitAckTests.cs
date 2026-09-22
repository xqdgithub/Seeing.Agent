using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 固化提交（CommitAsync）的失败语义：投递失败/渲染线程失效必须抛出可辨识异常
/// （而非静默丢弃却让上层推进账本）；正常投递成功不得抛出。
/// <b>不</b>启动真实渲染线程、<b>不</b>等待终端或按键。
/// </summary>
public sealed class SpectreTerminalSurfaceCommitAckTests
{
    [Fact]
    public void ResolveCommitFailure_Faulted_ShouldReturnInvalidOperationException()
        => SpectreTerminalSurface
            .ResolveCommitFailure(faulted: true, delivered: false)
            .Should().BeOfType<InvalidOperationException>();

    [Fact]
    public void ResolveCommitFailure_NotDelivered_ShouldReturnTimeoutException()
        => SpectreTerminalSurface
            .ResolveCommitFailure(faulted: false, delivered: false)
            .Should().BeOfType<TimeoutException>();

    [Fact]
    public void ResolveCommitFailure_Delivered_ShouldReturnNull()
        => SpectreTerminalSurface
            .ResolveCommitFailure(faulted: false, delivered: true)
            .Should().BeNull();

    [Fact]
    public void CommitAsync_WhenFaulted_ShouldThrowWithoutStartingRenderThread()
    {
        var surface = new SpectreTerminalSurface(CreateConsole());
        surface.Fault();

        // 块体 lambda 丢弃返回的 Task，使断言为同步 Action：CommitAsync 在失效时同步抛出。
        var act = () => { _ = surface.CommitAsync(new Text("x")); };

        act.Should().Throw<InvalidOperationException>();
    }

    private static IAnsiConsole CreateConsole()
    {
        // 非交互内存输出：即便意外启动渲染线程也不会阻塞真实终端。
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 80;
        console.Profile.Height = 24;
        return console;
    }
}
