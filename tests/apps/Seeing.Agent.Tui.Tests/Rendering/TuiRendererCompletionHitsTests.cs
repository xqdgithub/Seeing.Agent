using System.Text;
using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 内联补全渲染几何回归（spec §6.4，W1-C）：游标高亮、<see cref="DisplayText"/> 显示宽单物理行截断（M-5）、
/// 命中表 <c>CompletionHits</c> 与帧行布局一致且只含实画行（M-6）、帧代次透传；
/// 另含 surface 的 DSR 写点接线（<c>UpdateTarget</c> 后、<c>PlaceCaret</c> 前，受 MouseEnabled 门控）。
/// </summary>
public sealed class TuiRendererCompletionHitsTests
{
    private static readonly TuiRenderer Renderer = new(new TuiRenderOptions());

    private static TuiViewState ExecutingState()
        => new() { SessionId = "ses_1", AgentId = "build", IsExecuting = true };

    private static TuiInputEditorState Input(string text = "")
    {
        var input = new TuiInputEditorState();
        input.SetTextAndCursor(text, text.Length);
        return input;
    }

    private static string[] RenderLines(IRenderable renderable)
    {
        var text = new Fakes.FakeTerminalSurface().RenderToString(renderable);
        return text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void BuildActiveViewWithCaret_WithCompletions_ShouldEmitHitsMatchingFrameLayout()
    {
        // 行布局（自上而下）：候选 0/1/2 → 分隔线 → 输入行 → 状态行（末行）。
        // RowsFromBottom：候选 0 之下 = 2 候选 + 1 分隔 + 1 输入 + 1 状态 = 5。
        var completions = new[]
        {
            new TuiCompletionItem("/aa", "A"),
            new TuiCompletionItem("/bb", "B"),
            new TuiCompletionItem("/cc", "C"),
        };

        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(), Input("/a"), 100, completions: completions, frameGen: 42);

        frame.CompletionHits.Should().Equal(
            new TuiHitRegion(0, 5, 42),
            new TuiHitRegion(1, 4, 42),
            new TuiHitRegion(2, 3, 42));
        frame.FrameGen.Should().Be(42);
    }

    [Fact]
    public void BuildActiveViewWithCaret_WithMultiLineInput_ShouldCountEveryRowBelowCandidate()
    {
        // 输入两行（a\nb）：候选之下 = 1 候选余行 + 1 分隔 + 2 输入 + 1 状态。
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("a\nb"),
            100,
            completions: [new TuiCompletionItem("/x", "X"), new TuiCompletionItem("/y", "Y")],
            frameGen: 3);

        frame.CompletionHits.Should().Equal(
            new TuiHitRegion(0, 5, 3),
            new TuiHitRegion(1, 4, 3));
    }

    [Fact]
    public void BuildActiveViewWithCaret_WhenMaxLinesClipsCandidates_ShouldExcludeClippedRows()
    {
        // maxLines=5：可见窗为末 5 行（行距底 ≤4）；候选 0 的 RowsFromBottom=5 已被 TailClip 裁掉 → 不入表。
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/a"),
            100,
            maxLines: 5,
            completions:
            [
                new TuiCompletionItem("/aa", "A"),
                new TuiCompletionItem("/bb", "B"),
                new TuiCompletionItem("/cc", "C"),
            ]);

        frame.CompletionHits.Should().Equal(
            new TuiHitRegion(1, 4, 0),
            new TuiHitRegion(2, 3, 0));
        frame.Caret.Should().Be(new TuiCaret(1, 5), "插入点距末行 1，仍在可见窗内");
    }

    [Fact]
    public void BuildActiveViewWithCaret_WithoutCompletions_ShouldLeaveHitsNull()
    {
        var frame = Renderer.BuildActiveViewWithCaret(ExecutingState(), Input(), 100, frameGen: 7);

        frame.CompletionHits.Should().BeNull();
        frame.FrameGen.Should().Be(7);
    }

    [Fact]
    public void BuildCompletions_WithSelectedIndex_ShouldHighlightOnlyThatRow()
    {
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/"),
            100,
            completions:
            [
                new TuiCompletionItem("/aa", "A"),
                new TuiCompletionItem("/bb", "B"),
                new TuiCompletionItem("/cc", "C"),
            ],
            completionSelectedIndex: 1);

        var lines = RenderLines(frame.View);
        lines.Count(l => l.Contains('▸')).Should().Be(1);
        lines.Single(l => l.Contains('▸')).Should().StartWith("▸ ").And.Contain("/bb");
        lines.Should().Contain(l => !l.Contains('▸') && l.Contains("/aa"));
        lines.Should().Contain(l => !l.Contains('▸') && l.Contains("/cc"));
    }

    [Fact]
    public void BuildCompletions_WithoutSelection_ShouldNotRenderCursorGlyph()
    {
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/"),
            100,
            completions: [new TuiCompletionItem("/aa", "A")]);

        new Fakes.FakeTerminalSurface().RenderToString(frame.View).Should().NotContain("▸");
    }

    [Fact]
    public void BuildCompletions_WithOverwideRows_ShouldTruncateWithoutWrapping()
    {
        // 渲染 console 宽 120，候选块按 width=40 截断：任何折行都会让总行数 > 预期（候选行必须恒为 1 物理行）。
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/"),
            40,
            completions:
            [
                new TuiCompletionItem(new string('x', 60), new string('d', 300)),
                new TuiCompletionItem("/ok", "fine"),
            ]);

        var lines = RenderLines(frame.View);
        lines.Should().HaveCount(5, "2 候选 + 分隔 + 输入 + 状态各 1 行，无折行");

        var wide = lines.Single(l => l.Contains('x'));
        wide.Should().Contain("…");
        DisplayText.Width(wide).Should().BeLessThanOrEqualTo(39, "行宽 ≤ width-1（末列余量防自动换行）");
    }

    [Fact]
    public void BuildCompletions_WithCjkOverwideName_ShouldTruncateByDisplayWidth()
    {
        // 20 个 CJK（显示宽 40）→ nameCap=min(24, 30-5)=24 → 取 11 字（22 格）+ …（2 格）= 24。
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/"),
            30,
            completions: [new TuiCompletionItem(new string('中', 20), "描述")]);

        // 状态栏等行也可能含 CJK，用连续 5 字定位候选行（仅此行的名称列有长串中文）。
        var line = RenderLines(frame.View).Single(l => l.Contains(new string('中', 5)));
        line.Should().StartWith(new string('中', 11) + "…");
        DisplayText.Width(line).Should().BeLessThanOrEqualTo(29);
    }

    [Fact]
    public void BuildCompletions_ShouldPadNameColumnByDisplayWidthNotCharCount()
    {
        // "/中文" 3 字符显示宽 5（1+2×2）；"/ab" 显示宽 3 → 补 2 格再跟 1 分隔空格 = 3 空格（按字符数算会得 4，错误）。
        var frame = Renderer.BuildActiveViewWithCaret(
            ExecutingState(),
            Input("/"),
            100,
            completions:
            [
                new TuiCompletionItem("/中文", "CJK"),
                new TuiCompletionItem("/ab", "ASCII"),
            ]);

        var lines = RenderLines(frame.View);
        lines.Single(l => l.Contains("/ab")).Should().Contain("/ab" + new string(' ', 3) + "ASCII");
    }

    // —— surface DSR 写点接线（spec §6.4/§7.3）——

    [Fact]
    public async Task UpdateAsync_WithProbeGenAndMouseEnabled_ShouldWriteDsrAfterFrameBeforeCaret()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        var probe = new RecordingProbe();
        surface.ConfigureMouse(probe, mouseEnabled: true);
        try
        {
            await surface.UpdateAsync(new Text("FRAME"), new TuiCaret(1, 5), probeGen: 9, ct: TestContext.Current.CancellationToken);
            await WaitForAsync(() => writer.Text.Contains("\u001b[1A", StringComparison.Ordinal));

            var text = writer.Text;
            text.Should().Contain("FRAME");
            text.Should().Contain(SpectreTerminalSurface.DsrCursorReportQuery);

            // DSR 必须在整帧写完之后（光标仍在末行）、光标上移定位之前发出。
            text.IndexOf("FRAME", StringComparison.Ordinal)
                .Should().BeLessThan(text.IndexOf(SpectreTerminalSurface.DsrCursorReportQuery, StringComparison.Ordinal));
            text.IndexOf(SpectreTerminalSurface.DsrCursorReportQuery, StringComparison.Ordinal)
                .Should().BeLessThan(text.IndexOf("\u001b[1A", StringComparison.Ordinal));
            probe.LastExternalGen.Should().Be(9, "代次与引擎随帧传入同源（M-3）");
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    [Fact]
    public async Task UpdateAsync_WhenMouseDisabledOrProbeGenZero_ShouldNotWriteDsr()
    {
        var writer = new ThreadSafeWriter();
        var surface = new SpectreTerminalSurface(CreateConsole(writer));
        var probe = new RecordingProbe();
        surface.ConfigureMouse(probe, mouseEnabled: false);
        try
        {
            await surface.UpdateAsync(new Text("FRAME"), new TuiCaret(1, 5), probeGen: 3, ct: TestContext.Current.CancellationToken);
            await WaitForAsync(() => writer.Text.Contains("\u001b[1A", StringComparison.Ordinal));
            writer.Text.Should().NotContain(SpectreTerminalSurface.DsrCursorReportQuery);

            surface.ConfigureMouse(probe, mouseEnabled: true);
            await surface.UpdateAsync(new Text("FRAME2"), new TuiCaret(1, 5), ct: TestContext.Current.CancellationToken);
            await WaitForAsync(() => writer.Text.Contains("FRAME2", StringComparison.Ordinal));
            writer.Text.Should().NotContain(SpectreTerminalSurface.DsrCursorReportQuery, "probeGen=0（旧重载）不探测");
            probe.LastExternalGen.Should().Be(0);
        }
        finally
        {
            await surface.StopAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private static IAnsiConsole CreateConsole(TextWriter writer)
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

    /// <summary>记录外部指定代次探测并执行写回调的探针替身。</summary>
    private sealed class RecordingProbe : ITuiAnchorProbe
    {
        public long LastExternalGen { get; private set; }

        public void Report(int row)
        {
        }

        public bool TryGet(out int row, out long gen)
        {
            row = 0;
            gen = 0;
            return false;
        }

        public void Invalidate()
        {
        }

        public long BeginProbe(Action writeDsr) => 0;

        public void BeginProbe(long gen, Action writeDsr)
        {
            LastExternalGen = gen;
            writeDsr();
        }
    }

    /// <summary>渲染线程写、测试线程读的 <see cref="TextWriter"/>。</summary>
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
