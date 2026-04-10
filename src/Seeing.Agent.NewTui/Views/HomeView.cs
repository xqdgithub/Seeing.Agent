using Terminal.Gui;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.NewTui.Services;
using Seeing.Agent.Core;

namespace Seeing.Agent.NewTui.Views;

public class HomeView : Window
{
    private readonly AppState _state;
    private readonly AgentRunner _runner;
    private readonly AgentRegistry _registry;
    private TextField _inputField;
    
    public HomeView(AppState state, AgentRunner runner, AgentRegistry registry) : base("Seeing.Agent")
    {
        _state = state;
        _runner = runner;
        _registry = registry;
        
        Width = Dim.Fill();
        Height = Dim.Fill();
        
        // Logo
        var logo = new Label(GetLogo())
        {
            X = Pos.Center(),
            Y = Pos.Center() - 5
        };
        Add(logo);
        
        // 使用 TextField（单行输入，Enter 自动提交）
        _inputField = new TextField("")
        {
            X = Pos.Center(),
            Y = Pos.Center() + 2,
            Width = 60
        };
        Add(_inputField);
        
        // 提示信息
        var hint = new Label("Press Enter to send | Ctrl+H History | Ctrl+A Agent")
        {
            X = Pos.Center(),
            Y = Pos.Bottom(_inputField) + 1
        };
        Add(hint);
        
        // 全局快捷键（Ctrl+H, Ctrl+A）
        Application.Top.KeyPress += OnGlobalKeyPress;
    }
    
    /// <summary>
    /// 处理全局快捷键（Ctrl 组合键）
    /// </summary>
    private void OnGlobalKeyPress(View.KeyEventEventArgs e)
    {
        var key = e.KeyEvent.Key;
        
        if ((key & Key.CtrlMask) == Key.CtrlMask)
        {
            var baseKey = key & ~Key.CtrlMask;
            
            if (baseKey == Key.H)
            {
                e.Handled = true;
                ShowSessionList();
            }
            else if (baseKey == Key.A)
            {
                e.Handled = true;
                ShowAgentSelector();
            }
        }
    }
    
    /// <summary>
    /// 处理用户输入并发送消息
    /// </summary>
    public async Task HandleInputAsync()
    {
        var text = _inputField.Text.ToString()?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            _inputField.Text = "";
            
            // 进入 Session 视图
            var sessionView = new SessionView(_state, _runner);
            Application.Top.Add(sessionView);
            
            // 发送消息
            await _runner.SendMessageAsync(text);
        }
    }
    
    private void ShowSessionList()
    {
        var dialog = new Dialogs.SessionListDialog(_state);
        Application.Run(dialog);
    }
    
    private void ShowAgentSelector()
    {
        var dialog = new Dialogs.AgentSelectDialog(_state, _registry);
        Application.Run(dialog);
    }
    
    private static string GetLogo()
    {
        // SEEING ASCII Art
        return @"
   ██████╗██╗   ██╗██████╗ ███████╗██████╗ 
  ██╔════╝██║   ██║██╔══██╗██╔════╝██╔══██╗
  ██║     ██║   ██║██████╔╝█████╗  ██████╔╝
  ██║     ██║   ██║██╔══██╗██╔══╝  ██╔══██╗
  ╚██████╗╚██████╔╝██║  ██║███████╗██║  ██║
   ╚═════╝ ╚═════╝ ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝";
    }
}
