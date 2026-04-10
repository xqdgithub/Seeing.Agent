using Terminal.Gui;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.NewTui.Services;
using Seeing.Agent.NewTui.Components;

namespace Seeing.Agent.NewTui.Views;

public class SessionView : Window
{
    private readonly AppState _state;
    private readonly AgentRunner _runner;
    
    private MessageList _messageList;
    private TextField _inputField;
    private Label _statusLabel;
    
    public SessionView(AppState state, AgentRunner runner) : base("Session")
    {
        _state = state;
        _runner = runner;
        
        Width = Dim.Fill();
        Height = Dim.Fill();
        
        // 消息列表
        _messageList = new MessageList(state)
        {
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 4
        };
        Add(_messageList);
        
        // 使用 TextField（单行输入）
        _inputField = new TextField("")
        {
            Y = Pos.Bottom(_messageList),
            Width = Dim.Fill()
        };
        Add(_inputField);
        
        // 状态栏
        _statusLabel = new Label("Ready - Press Enter to send | Esc Back | Ctrl+C Cancel")
        {
            Y = Pos.Bottom(_inputField)
        };
        Add(_statusLabel);
        
        // 监听状态变化
        _state.StateChanged += UpdateStatus;
        
        // 全局快捷键（Esc, Ctrl+C）
        Application.Top.KeyPress += OnGlobalKeyPress;
    }
    
    private void UpdateStatus()
    {
        Application.MainLoop.Invoke(() =>
        {
            if (_state.IsProcessing)
            {
                _statusLabel.Text = $"Processing with {_state.CurrentAgent}... (Ctrl+C to cancel)";
            }
            else
            {
                _statusLabel.Text = $"Ready - Agent: {_state.CurrentAgent} | Enter to send";
            }
        });
    }
    
    /// <summary>
    /// 处理全局快捷键
    /// </summary>
    private void OnGlobalKeyPress(View.KeyEventEventArgs e)
    {
        var key = e.KeyEvent.Key;
        
        if (key == Key.Esc)
        {
            e.Handled = true;
            Application.Top.Remove(this);
        }
        
        if ((key & Key.CtrlMask) == Key.CtrlMask && (key & ~Key.CtrlMask) == Key.C)
        {
            e.Handled = true;
            _state.CancelProcessing();
        }
    }
    
    /// <summary>
    /// 处理输入并发送消息（由外部调用或 Enter 事件触发）
    /// </summary>
    public async Task HandleInputAsync()
    {
        if (_state.IsProcessing) return;
        
        var input = _inputField.Text.ToString()?.Trim();
        if (!string.IsNullOrEmpty(input))
        {
            _inputField.Text = "";
            await _runner.SendMessageAsync(input);
        }
    }
}
