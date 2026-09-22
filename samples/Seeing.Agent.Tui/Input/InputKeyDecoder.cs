namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 原始输入 → 语义动作（纯函数）。
/// </summary>
public static class InputKeyDecoder
{
    public static TuiKeyInput Decode(TuiRawInput input, bool pasteActive) => input switch
    {
        TuiRawText text => text.Value.Length == 0
            ? new TuiKeyInput(TuiInputAction.None)
            : new TuiKeyInput(TuiInputAction.InsertText, text.Value),
        TuiRawPasteStart or TuiRawPasteEnd => new TuiKeyInput(TuiInputAction.None),
        TuiRawEscape escape => DecodeEscape(escape.Sequence),
        TuiRawKey key => DecodeKey(key, pasteActive),
        _ => new TuiKeyInput(TuiInputAction.None),
    };

    private static TuiKeyInput DecodeEscape(string sequence) => sequence switch
    {
        "\x1b[A" => new TuiKeyInput(TuiInputAction.HistoryPrev),
        "\x1b[B" => new TuiKeyInput(TuiInputAction.HistoryNext),
        "\x1b[C" => new TuiKeyInput(TuiInputAction.MoveRight),
        "\x1b[D" => new TuiKeyInput(TuiInputAction.MoveLeft),
        "\x1b[3~" => new TuiKeyInput(TuiInputAction.DeleteForward),
        "\x1b[13;2u" => new TuiKeyInput(TuiInputAction.InsertNewline),
        "\x1b[13;2~" => new TuiKeyInput(TuiInputAction.InsertNewline),
        _ => new TuiKeyInput(TuiInputAction.None),
    };

    private static TuiKeyInput DecodeKey(TuiRawKey key, bool pasteActive)
    {
        if (key.Ctrl && key.Key == ConsoleKey.C)
            return new TuiKeyInput(TuiInputAction.Cancel);

        if (key.Ctrl && key.Key == ConsoleKey.D)
            return new TuiKeyInput(TuiInputAction.ExitRequested);

        if (key.KeyChar == '\r')
            return pasteActive
                ? new TuiKeyInput(TuiInputAction.InsertNewline)
                : new TuiKeyInput(TuiInputAction.Submit);

        if (key.KeyChar == '\n')
            return new TuiKeyInput(TuiInputAction.InsertNewline);

        return key.Key switch
        {
            ConsoleKey.Escape => new TuiKeyInput(TuiInputAction.Cancel, IsEscape: true),
            ConsoleKey.Tab => new TuiKeyInput(TuiInputAction.Complete),
            ConsoleKey.Backspace => new TuiKeyInput(TuiInputAction.Backspace),
            ConsoleKey.Delete => new TuiKeyInput(TuiInputAction.DeleteForward),
            ConsoleKey.LeftArrow => new TuiKeyInput(TuiInputAction.MoveLeft),
            ConsoleKey.RightArrow => new TuiKeyInput(TuiInputAction.MoveRight),
            ConsoleKey.UpArrow => new TuiKeyInput(TuiInputAction.HistoryPrev),
            ConsoleKey.DownArrow => new TuiKeyInput(TuiInputAction.HistoryNext),
            _ => key.KeyChar >= ' '
                ? new TuiKeyInput(TuiInputAction.InsertText, key.KeyChar.ToString())
                : new TuiKeyInput(TuiInputAction.None),
        };
    }
}
