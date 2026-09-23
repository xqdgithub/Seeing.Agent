using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

public class InputKeyDecoderTests
{
    private static readonly (string Name, TuiRawInput Input, bool PasteActive, TuiInputAction Action, string Text)[] Cases =
    [
        ("Enter(CR) 提交", new TuiRawKey(ConsoleKey.Enter, '\r', false, false, false), false, TuiInputAction.Submit, ""),
        ("Ctrl+J(LF) 插入换行", new TuiRawKey(ConsoleKey.Enter, '\n', false, false, false), false, TuiInputAction.InsertNewline, ""),
        ("粘贴态 CR 插入换行", new TuiRawKey(ConsoleKey.Enter, '\r', false, false, false), true, TuiInputAction.InsertNewline, ""),
        ("粘贴态 LF 插入换行", new TuiRawKey(ConsoleKey.Enter, '\n', false, false, false), true, TuiInputAction.InsertNewline, ""),
        ("Tab 补全", new TuiRawKey(ConsoleKey.Tab, '\t', false, false, false), false, TuiInputAction.Complete, ""),
        ("Esc 取消", new TuiRawKey(ConsoleKey.Escape, '\x1b', false, false, false), false, TuiInputAction.Cancel, ""),
        ("上箭头 历史上一条", new TuiRawEscape("\x1b[A"), false, TuiInputAction.HistoryPrev, ""),
        ("下箭头 历史下一条", new TuiRawEscape("\x1b[B"), false, TuiInputAction.HistoryNext, ""),
        ("右箭头 右移光标", new TuiRawEscape("\x1b[C"), false, TuiInputAction.MoveRight, ""),
        ("左箭头 左移光标", new TuiRawEscape("\x1b[D"), false, TuiInputAction.MoveLeft, ""),
        ("Delete 前删", new TuiRawEscape("\x1b[3~"), false, TuiInputAction.DeleteForward, ""),
        ("CSI-u Shift+Enter 插入换行", new TuiRawEscape("\x1b[13;2u"), false, TuiInputAction.InsertNewline, ""),
        ("Ctrl+C 取消", new TuiRawKey(ConsoleKey.C, '\u0003', true, false, false), false, TuiInputAction.Cancel, ""),
        ("Ctrl+D 退出", new TuiRawKey(ConsoleKey.D, '\u0004', true, false, false), false, TuiInputAction.ExitRequested, ""),
        ("可打印字符 插入文本", new TuiRawKey(ConsoleKey.A, 'a', false, false, false), false, TuiInputAction.InsertText, "a"),
        ("粘贴多字符 原样插入", new TuiRawText("abc"), false, TuiInputAction.InsertText, "abc"),
        ("粘贴含换行 原样插入", new TuiRawText("多行\n内容"), false, TuiInputAction.InsertText, "多行\n内容"),
        ("空文本 无动作", new TuiRawText(""), false, TuiInputAction.None, ""),
        ("粘贴开始 无动作", new TuiRawPasteStart(), false, TuiInputAction.None, ""),
        ("粘贴结束 无动作", new TuiRawPasteEnd(), false, TuiInputAction.None, ""),
        ("未知转义 无动作", new TuiRawEscape("\x1b[99z"), false, TuiInputAction.None, ""),
    ];

    [Fact]
    public void Decode_ShouldMapRawInputToExpectedAction()
    {
        foreach (var (name, input, pasteActive, action, text) in Cases)
        {
            var result = InputKeyDecoder.Decode(input, pasteActive);

            result.Action.Should().Be(action, name);
            result.Text.Should().Be(text, name);
        }
    }

    [Fact]
    public void Decode_ShouldDistinguishEscapeFromCtrlC()
    {
        // Esc 与 Ctrl+C 都映射 Cancel，但只有 Esc 需要「执行中二次确认取消」。
        var esc = InputKeyDecoder.Decode(new TuiRawKey(ConsoleKey.Escape, '\x1b', false, false, false), false);
        esc.Action.Should().Be(TuiInputAction.Cancel);
        esc.IsEscape.Should().BeTrue();

        var ctrlC = InputKeyDecoder.Decode(new TuiRawKey(ConsoleKey.C, '\u0003', true, false, false), false);
        ctrlC.Action.Should().Be(TuiInputAction.Cancel);
        ctrlC.IsEscape.Should().BeFalse();
    }

    [Fact]
    public void Decode_TuiRawMousePress_ShouldMapToMouseActionWithPayload()
    {
        var raw = new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Press, 10, 5);

        var result = InputKeyDecoder.Decode(raw, false);

        result.Action.Should().Be(TuiInputAction.Mouse);
        result.Mouse.Should().Be(raw);
        result.Text.Should().BeEmpty();
        result.IsEscape.Should().BeFalse();
    }

    [Fact]
    public void Decode_TuiRawMouseMotion_ShouldMapToMouseActionPreservingPhaseAndButton()
    {
        var raw = new TuiRawMouse(TuiMouseButton.WheelUp, TuiMousePhase.Motion, 4, 6);

        var result = InputKeyDecoder.Decode(raw, pasteActive: true);

        result.Action.Should().Be(TuiInputAction.Mouse);
        result.Mouse.Should().Be(raw);
    }
}
