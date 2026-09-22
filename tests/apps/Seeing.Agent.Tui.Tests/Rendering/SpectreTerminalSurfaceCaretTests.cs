using System.Text;
using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 活动区光标定位的端到端接线（含真实渲染线程与 Spectre <c>Live</c>）：
/// 每帧写完把物理光标移到编辑插入点（终端据此绘制输入法组合串），并在写下一帧/提交前放回末行。
/// </summary>
public sealed class SpectreTerminalSurfaceCaretTests
{
    private const string UpOneColumnFour = "\u001b[1A\r\u001b[4C";
    private const string DownTwo = "\u001b[2B";

    [Fact]
    public async Task UpdateAsync_WithCaret_ShouldMovePhysicalCursorToCaretCell()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        try
        {
            await surface.UpdateAsync(new Text("frame"), new TuiCaret(1, 5));

            await WaitForAsync(() => writer.Text.Contains(UpOneColumnFour, StringComparison.Ordinal));
            writer.Text.Should().Contain(UpOneColumnFour);
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    [Fact]
    public async Task CommitAsync_WhenCaretPlaced_ShouldRestoreCursorToFrameEndBeforeWritingCommitted()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        try
        {
            await surface.UpdateAsync(new Text("frame"), new TuiCaret(2, 4));
            await WaitForAsync(() => writer.Text.Contains("\u001b[2A\r", StringComparison.Ordinal));

            await surface.CommitAsync(new Text("COMMITTED"));
            await WaitForAsync(() => writer.Text.Contains("COMMITTED", StringComparison.Ordinal));

            var text = writer.Text;
            var restoreAt = text.IndexOf(DownTwo, StringComparison.Ordinal);
            var commitAt = text.IndexOf("COMMITTED", StringComparison.Ordinal);

            // 光标必须先回到活动区末行，Live 的擦除与随后的固化写入才不会错位。
            restoreAt.Should().BeGreaterThanOrEqualTo(0);
            restoreAt.Should().BeLessThan(commitAt);
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    [Fact]
    public async Task UpdateAsync_WithoutCaret_ShouldNotEmitCaretSequences()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        try
        {
            await surface.UpdateAsync(new Text("frame"));
            await WaitForAsync(() => writer.Text.Contains("frame", StringComparison.Ordinal));

            writer.Text.Should().NotContain(DownTwo);
            writer.Text.Should().NotContain(UpOneColumnFour);
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    [Fact]
    public async Task UpdateAsync_ShouldWriteFrameOncePerUpdate()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        try
        {
            await surface.UpdateAsync(new Text("FRAME"), new TuiCaret(1, 5));

            // 插入点定位写在整帧写完之后：此刻数帧文本出现次数，等价于数本次更新写了几遍帧。
            // Spectre 的 UpdateTarget 内部已 Refresh，若再补一次 Refresh 就会写两遍（双倍擦写终端）。
            await WaitForAsync(() => writer.Text.Contains(UpOneColumnFour, StringComparison.Ordinal));
            CountOccurrences(writer.Text, "FRAME").Should().Be(1);
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static IAnsiConsole CreateConsole(ThreadSafeWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 80;
        console.Profile.Height = 24;
        return console;
    }

    /// <summary>渲染线程写、测试线程读的 <see cref="StringWriter"/>。</summary>
    private sealed class ThreadSafeWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_gate)
                    return _buffer.ToString();
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
                _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;

            lock (_gate)
                _buffer.Append(value);
        }
    }
}
