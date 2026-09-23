using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Rendering.Prompts;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// <see cref="TuiListPrompt"/> 控件回归：真实 Spectre console（StringWriter）+ 注入按键通道
/// （键盘 + 鼠标 <see cref="TuiRawMouse"/>）+ fake 底锚探针，仿 <c>QuestionPromptTests</c> 驱动法。
/// 覆盖：单选/多选取舍、取消哨兵、hover/点击、未校准失效、擦画口径与 DSR 门控。
/// </summary>
public sealed class TuiListPromptTests
{
    private static readonly string[] Labels = ["apple", "banana", "cherry"];

    // 帧几何：title 1 行 + 3 项 = 4 行；底锚（列表末行）绝对行 20
    // → item0=18、item1=19、item2=20。
    private const int AnchorRow = 20;
    private const int Item0Row = AnchorRow - 2;
    private const int Item1Row = AnchorRow - 1;
    private const int Item2Row = AnchorRow;

    [Fact]
    public async Task SelectAsync_KeyboardSubmit_ShouldReturnFirstIndex()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(0);
        h.Output.Should().Contain("Choose").And.Contain("apple").And.Contain("banana");
    }

    [Fact]
    public async Task SelectAsync_MoveDownThenSubmit_ShouldReturnMovedIndex()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.HistoryNext), Key(TuiInputAction.HistoryNext), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(2);
    }

    [Fact]
    public async Task SelectAsync_CancelWithCancelIndex_ShouldReturnCancelIndex()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Cancel));

        var result = await RunSelectAsync(h, cancelIndex: 9);

        result.Should().Be(9);
    }

    [Fact]
    public async Task SelectAsync_CancelWithoutCancelIndex_ShouldReturnNull()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Cancel));

        var result = await RunSelectAsync(h, cancelIndex: null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectAsync_MouseLeftPressOnItem_ShouldConfirmThatIndex()
    {
        using var h = CreateHarness();
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, Item1Row));

        var result = await RunSelectAsync(h);

        result.Should().Be(1);
    }

    [Fact]
    public async Task SelectAsync_MouseMotionHover_ThenSubmit_ConfirmsHoveredIndex()
    {
        using var h = CreateHarness();
        Feed(h, Mouse(TuiMouseButton.None, TuiMousePhase.Motion, Item2Row), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(2);
        h.Output.Should().Contain("▸");
    }

    [Fact]
    public async Task SelectAsync_MouseRelease_ShouldBeIgnored()
    {
        using var h = CreateHarness();
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Release, Item2Row), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(0);
    }

    [Fact]
    public async Task SelectAsync_MousePressOutsideItems_ShouldNotConfirm()
    {
        using var h = CreateHarness();
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, 99), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(0);
    }

    [Fact]
    public async Task SelectAsync_NotCalibrated_MouseIgnoredButKeyboardWorks()
    {
        using var h = CreateHarness(calibrated: false);
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, Item2Row),
            Key(TuiInputAction.HistoryNext), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(1);
        h.Probe.BeginCount.Should().BeGreaterThan(0, "MouseEnabled 时每帧应发起 DSR 探测");
    }

    [Fact]
    public async Task SelectAsync_MouseDisabled_ShouldNotProbeAndIgnoreMouse()
    {
        using var h = CreateHarness(mouse: false);
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, Item2Row), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(0);
        h.Probe.BeginCount.Should().Be(0);
        h.Output.Should().NotContain("\u001b[6n");
    }

    [Fact]
    public async Task SelectAsync_NoKeysChannel_ShouldReturnCancelIndexWithoutBlocking()
    {
        using var h = CreateHarness();
        var ctx = CloneCtx(h, dropKeys: true);

        var task = TuiListPrompt.SelectAsync(ctx, "Choose", Labels, 10, 5, TestContext.Current.CancellationToken);

        (await task).Should().Be(5);
    }

    [Fact]
    public async Task SelectAsync_RedrawOnCursorMove_ShouldErasePreviousFrame()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.HistoryNext), Key(TuiInputAction.Submit));

        await RunSelectAsync(h);

        // 首帧 4 行；重画前 CUU(4-1) 回列表首行 + ESC[J 擦到屏尾。
        h.Output.Should().Contain("\u001b[3A").And.Contain("\u001b[J").And.Contain("\u001b[6n");

        // 帧末不得带尾换行：擦画序列前紧邻的必须是 EraseFrame 的前导 \r，
        // 否则（Rows 尾换行）光标落在末行下一行，每帧少擦一行 → 标题累积。
        h.Output.Should().NotContain("\n\r\u001b[3A", "帧末光标须停在末行，CUU 前不得有换行");
    }

    [Fact]
    public async Task SelectAsync_MarkupLikeTitleAndLabels_ShouldNotThrowAndShowRawText()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit));

        var ctx = CloneCtx(h);
        var result = await TuiListPrompt
            .SelectAsync(ctx, "选择 [模式]", ["[危险] rm -rf", "安全"], 10, null, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Should().Be(0);
        h.Output.Should().Contain("[危险] rm -rf");
    }

    [Fact]
    public async Task SelectAsync_PagedSubmit_ShouldConfirmIndexBeyondFirstWindow()
    {
        using var h = CreateHarness();
        var items = new[] { "item0", "item1", "item2", "item3", "item4" };
        Feed(h, Key(TuiInputAction.HistoryNext), Key(TuiInputAction.HistoryNext),
            Key(TuiInputAction.HistoryNext), Key(TuiInputAction.HistoryNext), Key(TuiInputAction.Submit));

        var ctx = CloneCtx(h);
        var result = await TuiListPrompt
            .SelectAsync(ctx, "Choose", items, pageSize: 3, cancelIndex: null, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Should().Be(4);
        h.Output.Should().Contain("item4");
    }

    [Fact]
    public async Task SelectAsync_CtCancelled_ShouldThrowOperationCanceled()
    {
        using var h = CreateHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TuiListPrompt
            .SelectAsync(h.Context, "Choose", Labels, 10, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MultipleAsync_SpaceTogglesAndSubmit_ShouldReturnSortedChecked()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.InsertText, " "),
            Key(TuiInputAction.HistoryNext), Key(TuiInputAction.HistoryNext),
            Key(TuiInputAction.InsertText, " "), Key(TuiInputAction.Submit));

        var result = await RunMultipleAsync(h);

        result.Should().Equal(0, 2);
        h.Output.Should().Contain("☑").And.Contain("☐");
    }

    [Fact]
    public async Task MultipleAsync_Required_EmptySubmitIgnored_UntilChecked()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit), Key(TuiInputAction.InsertText, " "), Key(TuiInputAction.Submit));

        var result = await RunMultipleAsync(h, notRequired: false);

        result.Should().Equal(0);
    }

    [Fact]
    public async Task MultipleAsync_NotRequired_EmptySubmit_ShouldReturnEmptyList()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit));

        var result = await RunMultipleAsync(h, notRequired: true);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleAsync_Cancel_ShouldReturnNull()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Cancel));

        var result = await RunMultipleAsync(h, cancelIndex: 9);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MultipleAsync_MouseClick_ShouldToggleItem()
    {
        using var h = CreateHarness();
        Feed(h, Mouse(TuiMouseButton.Left, TuiMousePhase.Press, Item1Row), Key(TuiInputAction.Submit));

        var result = await RunMultipleAsync(h);

        result.Should().Equal(1);
    }

    [Fact]
    public async Task MultipleAsync_PreChecked_ShouldSubmitPreCheckedWithCursorReset()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit));

        var result = await RunMultipleAsync(h, preChecked: [1, 2]);

        result.Should().Equal(1, 2);
    }

    [Fact]
    public async Task MultipleAsync_SpaceInSingleSelectMode_ShouldNotToggle()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.InsertText, " "), Key(TuiInputAction.Submit));

        var result = await RunSelectAsync(h);

        result.Should().Be(0);
    }

    [Fact]
    public async Task MultipleAsync_Rich_ShouldRenderHeaderNumberedCheckboxesDescriptionsAndHints()
    {
        using var h = CreateHarness();
        Feed(h, Key(TuiInputAction.Submit));

        var items = new List<TuiListItem>
        {
            new("命令行 / Shell", "执行命令与脚本"),
            new("Git", null),
        };

        var result = await TuiListPrompt
            .MultipleAsync(
                CloneCtx(h), "常用工具", "下面这些你平时哪些会用到？", items,
                pageSize: 6, preChecked: null, notRequired: true, cancelIndex: null,
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        h.Output.Should().Contain("常用工具");
        h.Output.Should().Contain("下面这些你平时哪些会用到？（可多选）");
        h.Output.Should().Contain("1. [ ] 命令行 / Shell");
        h.Output.Should().Contain("执行命令与脚本");
        h.Output.Should().Contain("2. [ ] Git");
        h.Output.Should().Contain("↑↓");
        h.Output.Should().Contain("Enter 确认");
    }

    private static TuiKeyInput Key(TuiInputAction action, string text = "") => new(action, text);

    private static TuiKeyInput Mouse(TuiMouseButton button, TuiMousePhase phase, int row)
        => new(TuiInputAction.Mouse, Mouse: new TuiRawMouse(button, phase, Col: 5, Row: row));

    private static void Feed(Harness h, params TuiKeyInput[] keys)
    {
        foreach (var key in keys)
            h.Keys.Writer.TryWrite(key).Should().BeTrue();
    }

    private static Task<int?> RunSelectAsync(Harness h, int? cancelIndex = null)
        => TuiListPrompt
            .SelectAsync(h.Context, "Choose", Labels, 10, cancelIndex, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static Task<IReadOnlyList<int>?> RunMultipleAsync(
        Harness h,
        IReadOnlyList<int>? preChecked = null,
        bool notRequired = true,
        int? cancelIndex = null)
        => TuiListPrompt
            .MultipleAsync(h.Context, "Choose", Labels, 10, preChecked, notRequired, cancelIndex,
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static TuiPromptContext CloneCtx(Harness h, bool dropKeys = false)
        => new()
        {
            Console = h.Console,
            Keys = dropKeys ? null : h.Keys.Reader,
            Anchor = h.Probe,
            MouseEnabled = h.Context.MouseEnabled,
            Width = h.Context.Width,
        };

    private static Harness CreateHarness(bool mouse = true, bool calibrated = true) => new(mouse, calibrated);

    private sealed class Harness : IDisposable
    {
        private readonly StringWriter _writer = new();

        public Harness(bool mouse, bool calibrated)
        {
            Probe = new FakeAnchorProbe(calibrated, AnchorRow);
            Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(_writer),
            });
            Console.Profile.Width = 40;
            Console.Profile.Height = 24;
            Context = new TuiPromptContext
            {
                Console = Console,
                Keys = Keys.Reader,
                Anchor = Probe,
                MouseEnabled = mouse,
                Width = 40,
            };
        }

        public IAnsiConsole Console { get; }

        public Channel<TuiKeyInput> Keys { get; } = Channel.CreateUnbounded<TuiKeyInput>();

        public FakeAnchorProbe Probe { get; }

        public TuiPromptContext Context { get; }

        public string Output => _writer.ToString();

        public void Dispose() => _writer.Dispose();
    }

    /// <summary>可控底锚：MouseEnabled 时控件每帧 <c>BeginProbe</c>；校准态由构造决定。</summary>
    private sealed class FakeAnchorProbe(bool calibrated, int anchorRow) : ITuiAnchorProbe
    {
        private long _gen;

        public int BeginCount { get; private set; }

        public long BeginProbe(Action writeDsr)
        {
            writeDsr();
            BeginCount++;
            return ++_gen;
        }

        public void BeginProbe(long gen, Action writeDsr)
        {
            writeDsr();
            BeginCount++;
            _gen = gen;
        }

        public void Report(int row)
        {
        }

        public bool TryGet(out int row, out long gen)
        {
            row = anchorRow;
            gen = _gen;
            return calibrated;
        }

        public void Invalidate()
        {
        }
    }
}
