using System.Text;
using Seeing.Agent.SpectreTui.Core.State;

namespace Seeing.Agent.SpectreTui.Services;

/// <summary>
/// 键盘输入处理服务 - 处理 Console 键盘事件和快捷键
/// </summary>
public class InputService
{
    private readonly InputState _state;

    /// <summary>
    /// 创建 InputService 实例
    /// </summary>
    /// <param name="state">输入状态管理</param>
    public InputService(InputState state)
    {
        _state = state;
    }

    // ========== 快捷键事件 ==========

    /// <summary>发送消息事件（Enter 或 Ctrl+Enter）</summary>
    public event Action<string>? OnSendMessage;

    /// <summary>打开命令面板事件（Ctrl+P）</summary>
    public event Action? OnOpenCommandPalette;

    /// <summary>取消当前任务事件（Ctrl+C）</summary>
    public event Action? OnCancelTask;

    /// <summary>关闭对话框/面板事件（Escape）</summary>
    public event Action? OnClosePanel;

    /// <summary>切换多行模式事件</summary>
    public event Action? OnToggleMultiline;

    // ========== 键盘处理 ==========

    /// <summary>
    /// 处理键盘输入（非阻塞式）
    /// </summary>
    /// <returns>是否处理了输入</returns>
    public bool ProcessInput()
    {
        try
        {
            if (!Console.KeyAvailable)
                return false;

            var keyInfo = Console.ReadKey(true);

            // 处理快捷键组合
            if (HandleHotKey(keyInfo))
                return true;

            // 处理普通字符输入
            HandleNormalInput(keyInfo);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Console.KeyAvailable/ReadKey 在非交互式终端会抛异常
            return false;
        }
    }

    /// <summary>
    /// 异步等待键盘输入（单次检查，不阻塞）
    /// </summary>
    /// <param name="timeoutMs">已废弃参数，保留签名兼容</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否处理了输入</returns>
    public async Task<bool> ProcessInputAsync(int timeoutMs = 50, CancellationToken cancellationToken = default)
    {
        // 非阻塞式检查：只检查一次，不无限等待
        // 如果没有输入，立即返回 false，让主循环继续刷新显示
        if (cancellationToken.IsCancellationRequested)
            return false;

        // 检查是否有键盘输入可用
        try
        {
            if (Console.KeyAvailable)
            {
                return ProcessInput();
            }
        }
        catch (InvalidOperationException)
        {
            // Console.KeyAvailable 在某些环境下会抛异常（无终端）
            // 静默处理，返回 false
            await Task.Delay(10, cancellationToken);
            return false;
        }

        // 没有输入时短暂等待后返回，避免空转
        await Task.Delay(1, cancellationToken);
        return false;
    }

    /// <summary>
    /// 处理快捷键组合
    /// </summary>
    /// <param name="keyInfo">按键信息</param>
    /// <returns>是否处理了快捷键</returns>
    private bool HandleHotKey(ConsoleKeyInfo keyInfo)
    {
        var key = keyInfo.Key;
        var modifiers = keyInfo.Modifiers;

        // Ctrl+P: 打开命令面板
        if (modifiers.HasFlag(ConsoleModifiers.Control) && key == ConsoleKey.P)
        {
            OnOpenCommandPalette?.Invoke();
            return true;
        }

        // Ctrl+C: 取消当前任务
        if (modifiers.HasFlag(ConsoleModifiers.Control) && key == ConsoleKey.C)
        {
            OnCancelTask?.Invoke();
            return true;
        }

        // Escape: 关闭对话框/面板
        if (key == ConsoleKey.Escape)
        {
            OnClosePanel?.Invoke();
            return true;
        }

        // Enter 键处理（区分单行/多行模式）
        if (key == ConsoleKey.Enter)
        {
            // Ctrl+Enter: 多行模式下发送消息
            if (modifiers.HasFlag(ConsoleModifiers.Control) || !_state.IsMultilineMode)
            {
                SendMessage();
                return true;
            }

            // 单行模式下 Enter 直接发送，多行模式下 Enter 添加换行
            if (_state.IsMultilineMode)
            {
                _state.AppendText(Environment.NewLine);
                return true;
            }
        }

        // 上下箭头: 输入历史导航
        if (key == ConsoleKey.UpArrow)
        {
            NavigateHistoryUp();
            return true;
        }

        if (key == ConsoleKey.DownArrow)
        {
            NavigateHistoryDown();
            return true;
        }

        // Ctrl+M: 切换多行模式
        if (modifiers.HasFlag(ConsoleModifiers.Control) && key == ConsoleKey.M)
        {
            _state.ToggleMultilineMode();
            OnToggleMultiline?.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 处理普通输入（字符、退格、左右箭头等）
    /// </summary>
    /// <param name="keyInfo">按键信息</param>
    private void HandleNormalInput(ConsoleKeyInfo keyInfo)
    {
        var key = keyInfo.Key;
        var modifiers = keyInfo.Modifiers;

        // 退格键: 删除光标前字符
        if (key == ConsoleKey.Backspace)
        {
            _state.DeleteCharBeforeCursor();
            return;
        }

        // Delete键: 删除光标位置字符
        if (key == ConsoleKey.Delete)
        {
            _state.DeleteCharAtCursor();
            return;
        }

        // 左箭头: 光标左移
        if (key == ConsoleKey.LeftArrow)
        {
            _state.MoveCursorLeft();
            return;
        }

        // 右箭头: 光标右移
        if (key == ConsoleKey.RightArrow)
        {
            _state.MoveCursorRight();
            return;
        }

        // Home键: 光标移到开头
        if (key == ConsoleKey.Home)
        {
            _state.MoveCursorToStart();
            return;
        }

        // End键: 光标移到末尾
        if (key == ConsoleKey.End)
        {
            _state.MoveCursorToEnd();
            return;
        }

        // 普通字符输入
        if (keyInfo.KeyChar != '\0' && !char.IsControl(keyInfo.KeyChar))
        {
            _state.AppendChar(keyInfo.KeyChar);
        }
    }

    /// <summary>
    /// 发送当前输入消息
    /// </summary>
    private void SendMessage()
    {
        var input = _state.CurrentInput.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            // 添加到历史记录
            _state.AddToHistory(input);

            // 触发发送事件
            OnSendMessage?.Invoke(input);

            // 清空输入
            _state.ClearInput();
        }
    }

    /// <summary>
    /// 向上导航历史记录
    /// </summary>
    private void NavigateHistoryUp()
    {
        var previous = _state.GetPreviousHistory();
        if (previous != null)
        {
            _state.SetInput(previous);
        }
    }

    /// <summary>
    /// 向下导航历史记录
    /// </summary>
    private void NavigateHistoryDown()
    {
        var next = _state.GetNextHistory();
        if (next != null)
        {
            _state.SetInput(next);
        }
    }

    // ========== 输入模式控制 ==========

    /// <summary>
    /// 设置多行模式
    /// </summary>
    /// <param name="enabled">是否启用多行模式</param>
    public void SetMultilineMode(bool enabled)
    {
        _state.SetMultilineMode(enabled);
    }

    /// <summary>
    /// 获取当前输入文本
    /// </summary>
    /// <returns>当前输入文本</returns>
    public string GetCurrentInput()
    {
        return _state.CurrentInput;
    }

    /// <summary>
    /// 获取输入状态（用于渲染）
    /// </summary>
    /// <returns>输入状态对象</returns>
    public InputState GetState()
    {
        return _state;
    }

    /// <summary>
    /// 清空当前输入
    /// </summary>
    public void ClearInput()
    {
        _state.ClearInput();
    }
}