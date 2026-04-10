using Terminal.Gui;
using Seeing.Agent.NewTui.State;
using Seeing.Agent.NewTui.Views;

namespace Seeing.Agent.NewTui;

public class App
{
    private readonly AppState _state;
    private readonly HomeView _homeView;
    private SessionView? _currentSession;
    
    public App(AppState state, HomeView homeView)
    {
        _state = state;
        _homeView = homeView;
    }
    
    public async Task RunAsync()
    {
        Application.Top.Add(_homeView);
        
        // 全局 Enter 键处理
        Application.Top.KeyPress += OnEnterKeyPress;
        
        Application.Run();
    }
    
    /// <summary>
    /// 处理 Enter 键提交
    /// </summary>
    private async void OnEnterKeyPress(View.KeyEventEventArgs e)
    {
        if (e.KeyEvent.Key != Key.Enter) return;
        
        // 判断当前活动视图
        var top = Application.Top;
        var focused = top.Focused;
        
        // 如果是 HomeView 的输入框
        if (top.Subviews.Contains(_homeView) && _homeView.HasFocus)
        {
            e.Handled = true;
            await _homeView.HandleInputAsync();
        }
        
        // 如果是 SessionView 的输入框
        _currentSession = top.Subviews.OfType<SessionView>().FirstOrDefault();
        if (_currentSession != null && _currentSession.HasFocus)
        {
            e.Handled = true;
            await _currentSession.HandleInputAsync();
        }
    }
}
