namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 输入编辑态（纯逻辑，不写终端）。由主循环读/改。
/// </summary>
public sealed class TuiInputEditorState
{
    private const int MaxHistory = 100;

    private readonly List<string> _history = [];
    private readonly List<string> _attachments = [];
    private int _historyIndex;

    public string Text { get; private set; } = string.Empty;

    public int Cursor { get; private set; }

    public bool PasteActive { get; private set; }

    public IReadOnlyList<string> Attachments => _attachments;

    public void SetPasteActive(bool active) => PasteActive = active;

    public void Apply(TuiKeyInput input)
    {
        switch (input.Action)
        {
            case TuiInputAction.InsertText:
                if (input.Text.Length == 0)
                    return;
                Text = Text.Insert(Cursor, input.Text);
                Cursor += input.Text.Length;
                break;

            case TuiInputAction.InsertNewline:
                Text = Text.Insert(Cursor, "\n");
                Cursor++;
                break;

            case TuiInputAction.Backspace:
                if (Cursor == 0)
                    return;
                Text = Text.Remove(Cursor - 1, 1);
                Cursor--;
                break;

            case TuiInputAction.DeleteForward:
                if (Cursor >= Text.Length)
                    return;
                Text = Text.Remove(Cursor, 1);
                break;

            case TuiInputAction.MoveLeft:
                if (Cursor > 0)
                    Cursor--;
                break;

            case TuiInputAction.MoveRight:
                if (Cursor < Text.Length)
                    Cursor++;
                break;

            case TuiInputAction.HistoryPrev:
                NavigateHistory(-1);
                break;

            case TuiInputAction.HistoryNext:
                NavigateHistory(1);
                break;

            case TuiInputAction.Submit:
                PushHistory(Text);
                break;

            case TuiInputAction.Cancel:
                if (Text.Length > 0)
                    Clear();
                break;
        }
    }

    public void SetText(string text)
    {
        Text = text;
        Cursor = text.Length;
    }

    /// <summary>设置文本与光标位置（越界自动收敛），不触碰历史指针与附件。</summary>
    public void SetTextAndCursor(string text, int cursor)
    {
        Text = text ?? string.Empty;
        Cursor = Math.Clamp(cursor, 0, Text.Length);
        _historyIndex = _history.Count;
    }

    public void Clear()
    {
        Text = string.Empty;
        Cursor = 0;
        _historyIndex = _history.Count;
    }

    public void AddAttachment(string display) => _attachments.Add(display);

    public bool RemoveAttachment(int index)
    {
        if (index < 0 || index >= _attachments.Count)
            return false;

        _attachments.RemoveAt(index);
        return true;
    }

    public void ClearAttachments() => _attachments.Clear();

    private void PushHistory(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && (_history.Count == 0 || _history[^1] != text))
        {
            _history.Add(text);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        _historyIndex = _history.Count;
    }

    private void NavigateHistory(int delta)
    {
        if (_history.Count == 0)
            return;

        var index = _historyIndex + delta;
        if (index < 0)
            index = 0;
        if (index > _history.Count)
            index = _history.Count;

        _historyIndex = index;
        SetText(_historyIndex >= _history.Count ? string.Empty : _history[_historyIndex]);
    }
}
