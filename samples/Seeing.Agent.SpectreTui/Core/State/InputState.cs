namespace Seeing.Agent.SpectreTui.Core.State;

/// <summary>
/// 输入状态管理 - 管理多行模式和输入历史
/// </summary>
public class InputState
{
    // ========== 输入缓冲 ==========

    /// <summary>当前输入文本</summary>
    private readonly System.Text.StringBuilder _inputBuffer = new();

    /// <summary>获取当前输入文本</summary>
    public string CurrentInput => _inputBuffer.ToString();

    /// <summary>输入文本长度</summary>
    public int InputLength => _inputBuffer.Length;

    /// <summary>光标位置（字符索引）</summary>
    public int CursorPosition { get; set; } = 0;

    // ========== 多行模式 ==========

    /// <summary>是否多行模式</summary>
    public bool IsMultilineMode { get; private set; } = false;

    /// <summary>切换多行模式</summary>
    public void ToggleMultilineMode()
    {
        IsMultilineMode = !IsMultilineMode;
        OnStateChanged?.Invoke();
    }

    /// <summary>设置多行模式</summary>
    public void SetMultilineMode(bool enabled)
    {
        if (IsMultilineMode != enabled)
        {
            IsMultilineMode = enabled;
            OnStateChanged?.Invoke();
        }
    }

    // ========== 输入历史 ==========

    /// <summary>历史记录列表</summary>
    private readonly List<string> _history = new();

    /// <summary>历史记录最大数量</summary>
    public int MaxHistorySize { get; set; } = 100;

    /// <summary>当前历史浏览索引</summary>
    private int _historyIndex = -1;

    /// <summary>历史记录数量</summary>
    public int HistoryCount => _history.Count;

    /// <summary>浏览历史时保存的临时输入</summary>
    private string? _tempInputBeforeNavigation;

    // ========== 输入方法 ==========

    /// <summary>追加字符到输入缓冲</summary>
    public void AppendChar(char c)
    {
        _inputBuffer.Insert(CursorPosition, c);
        CursorPosition++;
        OnStateChanged?.Invoke();
    }

    /// <summary>追加字符串到输入缓冲</summary>
    public void AppendText(string text)
    {
        _inputBuffer.Insert(CursorPosition, text);
        CursorPosition += text.Length;
        OnStateChanged?.Invoke();
    }

    /// <summary>删除光标前一个字符</summary>
    public void DeleteCharBeforeCursor()
    {
        if (CursorPosition > 0)
        {
            _inputBuffer.Remove(CursorPosition - 1, 1);
            CursorPosition--;
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>删除光标位置的字符</summary>
    public void DeleteCharAtCursor()
    {
        if (CursorPosition < _inputBuffer.Length)
        {
            _inputBuffer.Remove(CursorPosition, 1);
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>清空输入缓冲</summary>
    public void ClearInput()
    {
        _inputBuffer.Clear();
        CursorPosition = 0;
        _historyIndex = -1;
        _tempInputBeforeNavigation = null;
        OnStateChanged?.Invoke();
    }

    /// <summary>设置输入文本（用于历史导航恢复）</summary>
    public void SetInput(string text)
    {
        _inputBuffer.Clear();
        _inputBuffer.Append(text);
        CursorPosition = text.Length;
        OnStateChanged?.Invoke();
    }

    // ========== 历史管理方法 ==========

    /// <summary>添加输入到历史记录</summary>
    public void AddToHistory(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        // 避免重复添加相同的输入
        if (_history.Count > 0 && _history[^1] == input)
            return;

        _history.Add(input);

        // 限制历史记录大小
        if (_history.Count > MaxHistorySize)
            _history.RemoveAt(0);

        // 重置历史索引
        _historyIndex = -1;
        _tempInputBeforeNavigation = null;
    }

    /// <summary>获取上一条历史记录（向上箭头）</summary>
    public string? GetPreviousHistory()
    {
        if (_history.Count == 0)
            return null;

        // 第一次进入历史浏览时，保存当前输入
        if (_historyIndex == -1)
        {
            _tempInputBeforeNavigation = CurrentInput;
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }

        return _history[_historyIndex];
    }

    /// <summary>获取下一条历史记录（向下箭头）</summary>
    public string? GetNextHistory()
    {
        if (_history.Count == 0 || _historyIndex == -1)
            return null;

        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            return _history[_historyIndex];
        }
        else
        {
            // 到达末尾，恢复保存的临时输入
            _historyIndex = -1;
            return _tempInputBeforeNavigation ?? string.Empty;
        }
    }

    /// <summary>重置历史浏览索引</summary>
    public void ResetHistoryNavigation()
    {
        _historyIndex = -1;
        _tempInputBeforeNavigation = null;
    }

    /// <summary>清空历史记录</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _historyIndex = -1;
        _tempInputBeforeNavigation = null;
    }

    /// <summary>获取所有历史记录</summary>
    public IReadOnlyList<string> GetAllHistory() => _history.AsReadOnly();

    // ========== 光标移动 ==========

    /// <summary>光标左移</summary>
    public void MoveCursorLeft()
    {
        if (CursorPosition > 0)
            CursorPosition--;
    }

    /// <summary>光标右移</summary>
    public void MoveCursorRight()
    {
        if (CursorPosition < _inputBuffer.Length)
            CursorPosition++;
    }

    /// <summary>光标移到开头</summary>
    public void MoveCursorToStart()
    {
        CursorPosition = 0;
    }

    /// <summary>光标移到末尾</summary>
    public void MoveCursorToEnd()
    {
        CursorPosition = _inputBuffer.Length;
    }

    // ========== 事件 ==========

    /// <summary>状态变更事件</summary>
    public event Action? OnStateChanged;
}